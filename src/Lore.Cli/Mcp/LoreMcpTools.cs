using System.ComponentModel;
using System.Text;
using Lore.Cli.Client;
using Lore.Core.Git;
using Lore.Core.Models;
using ModelContextProtocol.Server;

namespace Lore.Cli.Mcp;

[McpServerToolType]
public sealed class LoreMcpTools
{
    private readonly ILoreClientFactory _factory;
    private readonly GitStalenessChecker _git;
    private readonly McpHarnessContext _harnessContext;

    public LoreMcpTools(ILoreClientFactory factory, GitStalenessChecker git, McpHarnessContext harnessContext)
    {
        _factory = factory;
        _git = git;
        _harnessContext = harnessContext;
    }

    [McpServerTool(Name = "lore_search"), Description("Hybrid semantic + keyword search across Lore memories (rules, processes/runbooks, app_ui specs, and notes).")]
    public async Task<string> SearchAsync(
        [Description("Natural language query or keywords")] string query,
        [Description("Optional filter: rule | process | app_ui | note")] string? type = null,
        [Description("Max results (default 5)")] int limit = 5,
        [Description("Working directory to resolve profile & repoId (defaults to current directory)")] string? cwd = null,
        [Description("Optional scope override: repo (default), global, or area:<name>")] string? scope = null,
        CancellationToken ct = default)
    {
        var workDir = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
        if (!_factory.TryResolve(workDir, _harnessContext.HarnessName, _harnessContext.ExplicitProfile, out var resolved, out var denied))
            return denied!;

        var repoId = _git.ResolveScope(workDir, scope);
        var parsedType = ParseType(type);

        var hits = await resolved!.Client.SearchAsync(
            new SearchRequest(repoId, query, parsedType, Math.Clamp(limit, 1, 15), workDir, _harnessContext.HarnessName ?? "mcp"), ct);

        if (hits.Count == 0)
            return $"No Lore memories matched \"{query}\" for scope `{repoId}` (profile `{resolved.Config.ActiveProfileName}`).";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {hits.Count} Lore memory hit(s) in `{repoId}` (profile `{resolved.Config.ActiveProfileName}`):");
        foreach (var h in hits)
        {
            var staleTag = h.Staleness?.IsStale == true ? $" [{h.Staleness.Message}]" : string.Empty;
            var slugPart = string.IsNullOrWhiteSpace(h.Slug) ? string.Empty : $" (`{h.Slug}`)";
            sb.AppendLine($"- **{h.Title}**{slugPart} — id:`{h.MemoryId}`, scope:`{h.RepoId}`, type:`{ToWire(h.Type)}`, authority:`{h.Authority.ToString().ToLowerInvariant()}`, score:`{h.VectorScore:F2}`{staleTag}");
            sb.AppendLine($"  {h.Snippet}");
        }
        sb.AppendLine("Call `lore_get(id)` to read the full Markdown document for any process or app_ui spec.");
        return sb.ToString();
    }

    [McpServerTool(Name = "lore_get"), Description("Fetch the complete Markdown document for a Lore memory by id or slug.")]
    public async Task<string> GetAsync(
        [Description("Memory ID (mem_...) or slug (e.g. ui/invoice-approval)")] string id,
        [Description("Working directory to resolve profile & repoId")] string? cwd = null,
        [Description("Optional scope override: repo (default), global, or area:<name>")] string? scope = null,
        CancellationToken ct = default)
    {
        var workDir = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
        if (!_factory.TryResolve(workDir, _harnessContext.HarnessName, _harnessContext.ExplicitProfile, out var resolved, out var denied))
            return denied!;

        var repoId = _git.ResolveScope(workDir, scope);
        var item = await resolved!.Client.GetAsync(repoId, id, ct);
        if (item is null)
        {
            var all = await resolved.Client.ListAsync(repoId, null, 100, ct);
            item = all.FirstOrDefault(m => string.Equals(m.Slug, id, StringComparison.OrdinalIgnoreCase));
        }

        if (item is null)
            return $"Lore memory '{id}' was not found.";

        var staleness = _git.CheckStaleness(workDir, item.VerifiedCommit, item.Paths);
        var sb = new StringBuilder();
        sb.AppendLine($"# {item.Title}");
        sb.AppendLine($"id: `{item.Id}` | profile: `{resolved.Config.ActiveProfileName}` | scope: `{item.RepoId}` | type: `{ToWire(item.Type)}` | authority: `{item.Authority.ToString().ToLowerInvariant()}` | updated: `{item.UpdatedAt:yyyy-MM-dd}`");
        if (!string.IsNullOrWhiteSpace(item.Slug))
            sb.AppendLine($"slug: `{item.Slug}`");
        if (item.Paths.Count > 0)
            sb.AppendLine($"paths: `{string.Join("`, `", item.Paths)}`");
        if (staleness?.IsStale == true)
            sb.AppendLine($"> WARNING: {staleness.Message}. Verify against code and call `lore_verify` if still accurate.");
        sb.AppendLine();
        sb.AppendLine(item.Body);
        return sb.ToString();
    }

    [McpServerTool(Name = "lore_save"), Description("Save or update a Lore memory (rule, process runbook, app_ui spec, or note). Upserts cleanly if slug matches.")]
    public async Task<string> SaveAsync(
        [Description("Memory type: rule | process | app_ui | note")] string type,
        [Description("Short descriptive title")] string title,
        [Description("Full Markdown content")] string body,
        [Description("1-2 sentence summary (used for TOC & search snippets)")] string? summary = null,
        [Description("Stable slug for updating living docs, e.g. ui/login or proc/deploy")] string? slug = null,
        [Description("Comma-separated file paths or globs for staleness detection, e.g. src/Web/**,src/Api/Auth.cs")] string? paths = null,
        [Description("Authority: user | verified | inferred (default: verified)")] string authority = "verified",
        [Description("Scope: repo (default) | global | area:<name>")] string scope = "repo",
        [Description("Working directory to resolve profile, repoId, and git commit")] string? cwd = null,
        CancellationToken ct = default)
    {
        var workDir = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
        if (!_factory.TryResolve(workDir, _harnessContext.HarnessName, _harnessContext.ExplicitProfile, out var resolved, out var denied))
            return denied!;

        var repoId = _git.ResolveScope(workDir, scope);
        var parsedType = ParseType(type) ?? MemoryType.Note;
        var parsedAuth = authority.ToLowerInvariant() switch
        {
            "user" => MemoryAuthority.User,
            "inferred" => MemoryAuthority.Inferred,
            _ => MemoryAuthority.Verified
        };

        var pathList = string.IsNullOrWhiteSpace(paths)
            ? []
            : paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var saved = await resolved!.Client.SaveAsync(new SaveMemoryRequest(
            repoId,
            parsedType,
            title,
            body,
            summary,
            slug,
            pathList,
            null,
            parsedAuth,
            _harnessContext.HarnessName ?? "mcp"), workDir, ct);

        resolved.Cache.InvalidateBootstrap(repoId);
        return $"Saved Lore {ToWire(saved.Type)} `{saved.Id}` ({saved.Title}) in `{saved.RepoId}` (profile `{resolved.Config.ActiveProfileName}`).";
    }

    [McpServerTool(Name = "lore_verify"), Description("Confirm a memory still matches current code/UI and refresh its verified git commit.")]
    public async Task<string> VerifyAsync(
        [Description("Memory ID (mem_...)")] string id,
        [Description("Working directory to resolve profile, repoId, and HEAD commit")] string? cwd = null,
        [Description("Optional scope override: repo (default), global, or area:<name>")] string? scope = null,
        CancellationToken ct = default)
    {
        var workDir = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
        if (!_factory.TryResolve(workDir, _harnessContext.HarnessName, _harnessContext.ExplicitProfile, out var resolved, out var denied))
            return denied!;

        var repoId = _git.ResolveScope(workDir, scope);
        var updated = await resolved!.Client.VerifyAsync(repoId, id, workDir, ct);
        return updated is null
            ? $"Memory '{id}' not found."
            : $"Verified `{updated.Id}` at commit `{updated.VerifiedCommit ?? "n/a"}`.";
    }

    [McpServerTool(Name = "lore_forget"), Description("Archive an obsolete or incorrect Lore memory so it no longer appears in search or session startup.")]
    public async Task<string> ForgetAsync(
        [Description("Memory ID (mem_...)")] string id,
        [Description("Working directory to resolve profile & repoId")] string? cwd = null,
        [Description("Optional scope override: repo (default), global, or area:<name>")] string? scope = null,
        CancellationToken ct = default)
    {
        var workDir = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
        if (!_factory.TryResolve(workDir, _harnessContext.HarnessName, _harnessContext.ExplicitProfile, out var resolved, out var denied))
            return denied!;

        var repoId = _git.ResolveScope(workDir, scope);
        await resolved!.Client.ArchiveAsync(repoId, id, ct);
        resolved.Cache.InvalidateBootstrap(repoId);
        return $"Archived Lore memory `{id}`.";
    }

    private static MemoryType? ParseType(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "rule" => MemoryType.Rule,
        "process" => MemoryType.Process,
        "app_ui" or "appui" => MemoryType.AppUi,
        "note" => MemoryType.Note,
        _ => null
    };

    private static string ToWire(MemoryType t) => t switch
    {
        MemoryType.Rule => "rule",
        MemoryType.Process => "process",
        MemoryType.AppUi => "app_ui",
        _ => "note"
    };
}
