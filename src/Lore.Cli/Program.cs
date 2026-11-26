using System.Diagnostics;
using Lore.Cli.Client;
using Lore.Cli.Hooks;
using Lore.Cli.Mcp;
using Lore.Core.Config;
using Lore.Core.Embeddings;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;
using Lore.Core.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var cmd = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

if (cmd == "mcp")
{
    var harness = GetFlag(rest, "--harness");
    var profile = GetFlag(rest, "--profile");

    var builder = Host.CreateApplicationBuilder(rest);
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton<GitStalenessChecker>();
    builder.Services.AddSingleton(new HttpClient());
    builder.Services.AddSingleton(new McpHarnessContext(harness, profile));
    builder.Services.AddSingleton<ILoreClientFactory>(sp =>
        new LoreClientFactory(sp.GetRequiredService<GitStalenessChecker>(), sp.GetRequiredService<HttpClient>(), longLived: true));
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<LoreMcpTools>();
    await builder.Build().RunAsync();
    return 0;
}

var services = new ServiceCollection();
services.AddSingleton<GitStalenessChecker>();
services.AddSingleton(new HttpClient());
services.AddSingleton<ILoreClientFactory>(sp =>
    new LoreClientFactory(sp.GetRequiredService<GitStalenessChecker>(), sp.GetRequiredService<HttpClient>(), longLived: false));
services.AddSingleton<HookRunner>();
await using var sp = services.BuildServiceProvider();

switch (cmd)
{
    case "hook":
        return await RunHookAsync(sp, rest);
    case "capture":
        return await RunCaptureAsync(sp, rest);
    case "search":
        return await RunSearchAsync(sp, rest);
    case "get":
        return await RunGetAsync(sp, rest);
    case "save":
        return await RunSaveAsync(sp, rest);
    case "list":
        return await RunListAsync(sp, rest);
    case "verify":
        return await RunVerifyAsync(sp, rest);
    case "forget":
        return await RunForgetAsync(sp, rest);
    case "stats":
        return await RunStatsAsync(sp, rest);
    case "doctor":
        return await RunDoctorAsync(sp, rest);
    case "profile":
        return RunProfile(sp, rest);
    case "configure":
        return RunConfigure(rest);
    default:
        PrintUsage();
        return 1;
}

static ResolvedProfile ResolveOrThrow(IServiceProvider sp, string? cwd, string? profile, string? harness = null)
{
    var factory = sp.GetRequiredService<ILoreClientFactory>();
    if (!factory.TryResolve(cwd, harness, profile, out var resolved, out var denied))
        throw new InvalidOperationException(denied ?? "Access denied for harness.");
    return resolved!;
}

static async Task<int> RunHookAsync(IServiceProvider sp, string[] args)
{
    if (args.Length < 2) return 1;
    var stdin = await Console.In.ReadToEndAsync();
    var runner = sp.GetRequiredService<HookRunner>();
    var output = await runner.HandleAsync(args[0], args[1], stdin);
    if (!string.IsNullOrEmpty(output))
        Console.WriteLine(output);
    return 0;
}

static async Task<int> RunCaptureAsync(IServiceProvider sp, string[] args)
{
    var harness = GetFlag(args, "--harness") ?? "agy";
    var transcript = GetFlag(args, "--transcript");
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var profile = GetFlag(args, "--profile");
    var sinceStep = int.TryParse(GetFlag(args, "--since-step"), out var s) ? s : -1;
    var sessionId = GetFlag(args, "--session-id");
    var maxStep = int.TryParse(GetFlag(args, "--max-step"), out var m) ? m : -1;

    if (string.IsNullOrWhiteSpace(transcript) || !File.Exists(transcript))
    {
        Console.Error.WriteLine("Missing or nonexistent --transcript file.");
        return 1;
    }

    var resolved = ResolveOrThrow(sp, cwd, profile, harness);
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));
    var reader = TranscriptReaderFactory.ForHarness(harness);
    var entries = reader.Read(transcript, sinceStep);
    var slices = TranscriptPreFilter.Filter(entries);

    var report = await resolved.Client.ExtractFromSlicesAsync(repoId, harness, cwd, slices);

    if (!string.IsNullOrWhiteSpace(sessionId))
    {
        var state = resolved.Cache.LoadSessionState(sessionId);
        state.LastCapturedStep = Math.Max(state.LastCapturedStep, maxStep >= 0 ? maxStep : entries.DefaultIfEmpty().Max(e => e?.StepIndex ?? -1));
        resolved.Cache.SaveSessionState(state);
        resolved.Cache.InvalidateBootstrap(repoId);
    }

    Console.WriteLine($"Captured {report.CandidateCount} candidate(s) from {report.FilteredSliceCount} slice(s) ({entries.Count} entries read, profile={resolved.Config.ActiveProfileName}):");
    foreach (var o in report.Outcomes)
        Console.WriteLine($"  [{o.Action}] {o.Title} ({o.MemoryId ?? "-"}) — {o.Reason}");

    return 0;
}

static async Task<int> RunSearchAsync(IServiceProvider sp, string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: lore search <query> [--type rule|process|app_ui|note] [--limit N] [--cwd dir] [--scope s] [--profile p]"); return 1; }
    var query = args[0];
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var profile = GetFlag(args, "--profile");
    var typeStr = GetFlag(args, "--type");
    var limit = int.TryParse(GetFlag(args, "--limit"), out var l) ? l : 5;

    var resolved = ResolveOrThrow(sp, cwd, profile);
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));

    var sw = Stopwatch.StartNew();
    var hits = await resolved.Client.SearchAsync(new SearchRequest(repoId, query, ParseType(typeStr), limit, cwd, "cli"));
    sw.Stop();

    Console.WriteLine($"Profile: {resolved.Config.ActiveProfileName} | Scope: {repoId} | {hits.Count} hit(s) in {sw.ElapsedMilliseconds}ms");
    foreach (var h in hits)
    {
        var stale = h.Staleness?.IsStale == true ? $" [{h.Staleness.Message}]" : string.Empty;
        Console.WriteLine($"- {h.IdDisplay()} | {h.Type} | score={h.VectorScore:F3} | {h.Title}{stale}");
        Console.WriteLine($"  {h.Snippet}");
    }
    return 0;
}

static async Task<int> RunGetAsync(IServiceProvider sp, string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: lore get <id|slug> [--cwd dir] [--scope s] [--profile p]"); return 1; }
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));

    var item = await resolved.Client.GetAsync(repoId, args[0]);
    if (item is null)
    {
        var list = await resolved.Client.ListAsync(repoId, null, 100);
        item = list.FirstOrDefault(x => string.Equals(x.Slug, args[0], StringComparison.OrdinalIgnoreCase));
    }
    if (item is null) { Console.Error.WriteLine($"Not found: {args[0]}"); return 1; }
    Console.WriteLine($"# {item.Title}\nID: {item.Id} | Profile: {resolved.Config.ActiveProfileName} | Scope: {item.RepoId} | Type: {item.Type} | Authority: {item.Authority} | Commit: {item.VerifiedCommit ?? "n/a"}\n");
    Console.WriteLine(item.Body);
    return 0;
}

static async Task<int> RunSaveAsync(IServiceProvider sp, string[] args)
{
    var type = ParseType(GetFlag(args, "--type")) ?? MemoryType.Note;
    var title = GetFlag(args, "--title") ?? throw new ArgumentException("--title is required");
    var body = GetFlag(args, "--body");
    var bodyFile = GetFlag(args, "--file");
    if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(bodyFile))
        body = await File.ReadAllTextAsync(bodyFile);
    if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("--body or --file is required");

    var summary = GetFlag(args, "--summary");
    var slug = GetFlag(args, "--slug");
    var pathsRaw = GetFlag(args, "--paths");
    var paths = string.IsNullOrWhiteSpace(pathsRaw) ? [] : pathsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    var auth = (GetFlag(args, "--authority") ?? "verified").ToLowerInvariant() switch
    {
        "user" => MemoryAuthority.User,
        "inferred" => MemoryAuthority.Inferred,
        _ => MemoryAuthority.Verified
    };

    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var git = sp.GetRequiredService<GitStalenessChecker>();

    var scopeArg = args.Contains("--global") ? "global" : GetFlag(args, "--scope");
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, scopeArg);

    var saved = await resolved.Client.SaveAsync(
        new SaveMemoryRequest(repoId, type, title, body, summary, slug, paths, null, auth, "cli"), cwd);
    resolved.Cache.InvalidateBootstrap(repoId);
    Console.WriteLine($"Saved {saved.Id} ({saved.Type}) in {saved.RepoId} [profile: {resolved.Config.ActiveProfileName}]: {saved.Title}");
    return 0;
}

static async Task<int> RunListAsync(IServiceProvider sp, string[] args)
{
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));
    var type = ParseType(GetFlag(args, "--type"));
    var items = await resolved.Client.ListAsync(repoId, type, 100);
    Console.WriteLine($"Profile: {resolved.Config.ActiveProfileName} | Scope: {repoId} | {items.Count} active memory item(s)");
    foreach (var m in items)
        Console.WriteLine($"- {m.Id} | {m.RepoId} | {m.Type} | {m.Authority} | {m.Title}");
    return 0;
}

static async Task<int> RunVerifyAsync(IServiceProvider sp, string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: lore verify <id> [--cwd dir] [--scope s] [--profile p]"); return 1; }
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));
    var item = await resolved.Client.VerifyAsync(repoId, args[0], cwd);
    if (item is null) { Console.Error.WriteLine($"Not found: {args[0]}"); return 1; }
    Console.WriteLine($"Verified {item.Id} at commit {item.VerifiedCommit ?? "n/a"}");
    return 0;
}

static async Task<int> RunForgetAsync(IServiceProvider sp, string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: lore forget <id> [--cwd dir] [--scope s] [--profile p]"); return 1; }
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));
    await resolved.Client.ArchiveAsync(repoId, args[0]);
    resolved.Cache.InvalidateBootstrap(repoId);
    Console.WriteLine($"Archived {args[0]}");
    return 0;
}

static async Task<int> RunStatsAsync(IServiceProvider sp, string[] args)
{
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var repoId = GetFlag(args, "--repo") ?? git.ResolveScope(cwd, GetFlag(args, "--scope"));
    var stats = await resolved.Client.GetStatsAsync(repoId);
    Console.WriteLine($"Lore Stats for {stats.RepoId} (profile: {resolved.Config.ActiveProfileName})");
    Console.WriteLine($"  Active memories: {stats.TotalActiveMemories} ({string.Join(", ", stats.CountsByType.Select(kv => $"{kv.Key}:{kv.Value}"))})");
    Console.WriteLine($"  Searches: {stats.SearchCount} (avg {stats.AvgSearchLatencyMs}ms)");
    Console.WriteLine($"  Prompt push: {stats.PromptPushInjectedCount} injected, {stats.PromptPushSkippedCount} skipped");
    Console.WriteLine($"  Captures: {stats.CaptureCreatedCount} created, {stats.CaptureMergedCount} merged, {stats.CaptureSupersededCount} superseded, {stats.CaptureSkippedCount} skipped");
    return 0;
}

static async Task<int> RunDoctorAsync(IServiceProvider sp, string[] args)
{
    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var resolved = ResolveOrThrow(sp, cwd, GetFlag(args, "--profile"));
    var cfg = resolved.Config;

    Console.WriteLine($"Lore Doctor (profile: {cfg.ActiveProfileName}, mode: {(cfg.IsApiMode ? $"API -> {cfg.ApiUrl}" : "Direct")})");
    if (cfg.IsApiMode)
    {
        var http = sp.GetRequiredService<HttpClient>();
        var baseUrl = cfg.ApiUrl!.TrimEnd('/');
        var res = await http.GetStringAsync($"{baseUrl}/health");
        Console.WriteLine($"  API health: {res}");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/doctor");
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        using var docResp = await http.SendAsync(req);
        docResp.EnsureSuccessStatusCode();
        var docJson = await docResp.Content.ReadAsStringAsync();
        Console.WriteLine($"  Remote doctor: {docJson}");
        return 0;
    }

    var sw = Stopwatch.StartNew();
    await resolved.DirectService!.EnsureSchemaAsync();
    Console.WriteLine($"  Cosmos DB schema ready ({cfg.CosmosEndpoint} / {cfg.CosmosDatabase}) in {sw.ElapsedMilliseconds}ms");

    sw.Restart();
    var cred = LoreConfig.CreateAzureCredential();
    var embedder = new AzureOpenAiEmbeddingProvider(cfg.AzureOpenAiEndpoint!, cfg.AzureOpenAiEmbeddingDeployment, cred, cfg.EmbeddingDimensions);
    var embed = await embedder.EmbedAsync("lore health check");
    Console.WriteLine($"  Azure OpenAI embedding ({cfg.AzureOpenAiEmbeddingDeployment}, {embed.Length}d) in {sw.ElapsedMilliseconds}ms");

    if (!string.IsNullOrWhiteSpace(cfg.AnthropicApiKey))
    {
        sw.Restart();
        var llm = new AnthropicLlmClient(sp.GetRequiredService<HttpClient>(), cfg.AnthropicApiKey, cfg.AnthropicModel);
        var reply = await llm.CompleteAsync("Reply with OK only.", "ping", 16);
        Console.WriteLine($"  Anthropic ({cfg.AnthropicModel}) replied '{reply.Trim()}' in {sw.ElapsedMilliseconds}ms");
    }
    else
    {
        Console.WriteLine("  Anthropic: SKIPPED (no ANTHROPIC_API_KEY configured)");
    }

    return 0;
}

static int RunProfile(IServiceProvider sp, string[] args)
{
    var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
    if (sub != "show")
    {
        Console.Error.WriteLine("Usage: lore profile show [--cwd <dir>] [--harness <h>] [--profile <name>]");
        return 1;
    }

    var cwd = GetFlag(args, "--cwd") ?? Directory.GetCurrentDirectory();
    var harness = GetFlag(args, "--harness");
    var explicitProfile = GetFlag(args, "--profile");

    var factory = sp.GetRequiredService<ILoreClientFactory>();
    var git = sp.GetRequiredService<GitStalenessChecker>();
    var cfg = factory.InspectConfig(cwd, explicitProfile);
    var repoId = git.ResolveRepoId(cwd);
    var allowed = cfg.IsHarnessAllowed(harness);

    Console.WriteLine($"Profile: {cfg.ActiveProfileName}");
    Console.WriteLine($"CWD: {cwd}");
    Console.WriteLine($"RepoId: {repoId}");
    Console.WriteLine($"Mode: {(cfg.IsApiMode ? $"API ({cfg.ApiUrl})" : $"Direct ({cfg.CosmosEndpoint} / db={cfg.CosmosDatabase})")}");
    Console.WriteLine($"CacheDir: {cfg.GetProfileDataDirectory()}");
    Console.WriteLine($"AllowedHarnesses: {(cfg.AllowedHarnesses is { Count: > 0 } ? string.Join(", ", cfg.AllowedHarnesses) : "all")}");
    if (!string.IsNullOrWhiteSpace(harness))
        Console.WriteLine($"Harness '{harness}' allowed: {allowed}");
    return allowed ? 0 : 2;
}

static int RunConfigure(string[] args)
{
    if (args.Length == 0) { Console.Error.WriteLine("Usage: lore configure <agy|claude|codex> [--workspace <dir> | --global]"); return 1; }
    var harness = args[0];
    var workspace = GetFlag(args, "--workspace");
    var global = args.Contains("--global") || string.IsNullOrWhiteSpace(workspace);
    var path = HarnessConfigure.Configure(harness, workspace, global);
    Console.WriteLine($"Configured {harness}: {path}");
    return 0;
}

static string? GetFlag(string[] args, string flag)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static MemoryType? ParseType(string? s) => s?.Trim().ToLowerInvariant() switch
{
    "rule" => MemoryType.Rule,
    "process" => MemoryType.Process,
    "app_ui" or "appui" => MemoryType.AppUi,
    "note" => MemoryType.Note,
    _ => null
};

static void PrintUsage() => Console.WriteLine("""
    lore — shared coding agent memory
    Commands:
      lore mcp [--harness <agy|claude|codex>] [--profile <name>]
      lore hook <agy|claude|codex> <session-start|prompt-push|stop|pre-compact>
      lore capture --harness <h> --transcript <path> [--cwd dir] [--scope s] [--profile p] [--since-step N]
      lore search "<query>" [--type rule|process|app_ui|note] [--limit N] [--scope s] [--profile p]
      lore get <id|slug> [--scope s] [--profile p]
      lore save --type <type> --title "<title>" (--body "<md>" | --file <path>) [--slug s] [--paths a,b] [--scope repo|global|area:<name>] [--profile p]
      lore list [--type <type>] [--scope s] [--profile p]
      lore verify <id> [--scope s] [--profile p]
      lore forget <id> [--scope s] [--profile p]
      lore stats [--scope s] [--profile p]
      lore profile show [--cwd <dir>] [--harness <h>] [--profile p]
      lore doctor [--cwd <dir>] [--profile p]
      lore configure <agy|claude|codex> [--workspace <dir> | --global]
    """);

internal static class HitDisplayExtensions
{
    public static string IdDisplay(this SearchHit hit) => string.IsNullOrWhiteSpace(hit.Slug) ? hit.MemoryId : $"{hit.MemoryId} ({hit.Slug})";
}
