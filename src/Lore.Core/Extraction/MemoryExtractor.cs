using System.Net.Http.Headers;
using System.Text;
using Lore.Core.Models;
using Lore.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lore.Core.Extraction;

public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2048, CancellationToken ct = default);
}

public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;

    public AnthropicLlmClient(HttpClient http, string apiKey, string model = "claude-haiku-5-5")
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2048, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            max_tokens = maxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API returned {(int)resp.StatusCode}: {raw}");

        var json = JObject.Parse(raw);
        var blocks = json["content"] as JArray;
        var text = blocks?
            .Where(b => b["type"]?.ToString() == "text")
            .Select(b => b["text"]?.ToString())
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        return text ?? string.Empty;
    }
}

public sealed class MemoryExtractor
{
    private readonly ILlmClient _llm;
    private readonly ILoreStoreOperations _lore;

    public MemoryExtractor(ILlmClient llm, ILoreStoreOperations lore)
    {
        _llm = llm;
        _lore = lore;
    }

    public async Task<ExtractionReport> ExtractAndStoreAsync(
        string repoId,
        string harness,
        string? cwd,
        IReadOnlyList<FilteredSlice> slices,
        CancellationToken ct = default)
    {
        if (slices.Count == 0)
            return new ExtractionReport(0, 0, 0, []);

        var candidates = await ExtractCandidatesAsync(repoId, slices, ct);
        var outcomes = new List<CaptureOutcome>();

        foreach (var candidate in candidates)
        {
            var outcome = await ReconcileAndStoreAsync(repoId, harness, cwd, candidate, ct);
            outcomes.Add(outcome);
            await _lore.LogCaptureOutcomeAsync(repoId, harness, outcome.Action, outcome.Title, ct);
        }

        return new ExtractionReport(slices.Count, slices.Count, candidates.Count, outcomes);
    }

    private async Task<List<ExtractionCandidate>> ExtractCandidatesAsync(
        string repoId,
        IReadOnlyList<FilteredSlice> slices,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {repoId}");
        sb.AppendLine("Filtered session evidence:");
        foreach (var s in slices)
        {
            sb.AppendLine($"--- [{s.Category} steps {s.StartStep}-{s.EndStep}] ---");
            sb.AppendLine(s.Text);
        }

        const string systemPrompt = """
            You extract ONLY durable, reusable knowledge from coding agent session slices.
            Return ONLY valid JSON: { "candidates": [ ... ] } (0 to 5 items; return empty array if nothing durable was learned).
            Do NOT extract ephemeral task progress, one-off file edits, or things obvious from reading a single file.

            Types:
            - "rule": Hard standing rules/constraints/traps stated or confirmed by the user (authority: "user" if user stated it, else "verified").
            - "process": Multi-step procedures/runbooks (login, build, deploy, DB setup, E2E test steps). Structure body with ## Overview, ## Steps, ## Gotchas.
            - "app_ui": Observed application or UI behavior (routes, screens, buttons/modals, roles, state transitions, quirks). Structure body with ## Entry Point, ## Visible UI & States, ## Flow, ## Quirks.
            - "note": Concrete bug root cause, architectural decision + rationale, or environment gotcha.

            Scope:
            - "repo" (default): Specific to this repository's codebase, UI, build, or endpoints.
            - "global": Machine-wide, environment-wide, or cross-repo rule/process/preference NOT tied to a single repo (e.g., local Docker rules, shared tooling conventions).

            Each candidate schema:
            {
              "type": "rule" | "process" | "app_ui" | "note",
              "scope": "repo" | "global",
              "title": "concise specific title",
              "summary": "1-2 sentence summary",
              "body": "clean markdown body",
              "slug": "optional stable slug like ui/invoice-approval or proc/local-login",
              "paths": ["optional/file/or/glob/**"],
              "tags": ["optional", "tags"],
              "authority": "user" | "verified" | "inferred"
            }
            """;

        var raw = await _llm.CompleteAsync(systemPrompt, sb.ToString(), 2200, ct);
        return ParseCandidates(raw);
    }

    private async Task<CaptureOutcome> ReconcileAndStoreAsync(
        string repoId,
        string harness,
        string? cwd,
        ExtractionCandidate candidate,
        CancellationToken ct)
    {
        var targetRepoId = string.Equals(candidate.Scope, "global", StringComparison.OrdinalIgnoreCase)
            ? "_global"
            : repoId;

        var existingBySlug = !string.IsNullOrWhiteSpace(candidate.Slug)
            ? await _lore.FindBySlugAsync(targetRepoId, candidate.Slug, ct)
            : null;

        var searchHits = await _lore.SearchAsync(
            new SearchRequest(targetRepoId, $"{candidate.Title}\n{candidate.Summary}", null, 3, cwd, harness), ct);

        MemoryItem? match = existingBySlug;
        if (match is null)
        {
            var topHit = searchHits.FirstOrDefault(h => h.VectorScore >= 0.72);
            if (topHit is not null)
                match = await _lore.GetAsync(targetRepoId, topHit.MemoryId, ct);
        }

        if (match is null)
        {
            var created = await _lore.SaveAsync(new SaveMemoryRequest(
                targetRepoId,
                candidate.Type,
                candidate.Title,
                candidate.Body,
                candidate.Summary,
                candidate.Slug,
                candidate.Paths,
                candidate.Tags,
                candidate.Authority,
                harness), cwd, ct);

            return new CaptureOutcome("create", created.Id, created.Title, $"No close existing match (scope={targetRepoId}).");
        }

        // Hard safety guard: inferred agent guesses may never overwrite or supersede user-stated rules.
        if (match.Authority == MemoryAuthority.User && candidate.Authority == MemoryAuthority.Inferred)
        {
            return new CaptureOutcome("skip", match.Id, candidate.Title, "Protected user-authority memory from inferred overwrite.");
        }

        var decision = await DecideMergeOrSupersedeAsync(match, candidate, ct);
        if (decision.Action == "skip")
            return new CaptureOutcome("skip", match.Id, candidate.Title, decision.Reason);

        if (decision.Action == "supersede")
        {
            var replacement = await _lore.SaveAsync(new SaveMemoryRequest(
                match.RepoId,
                candidate.Type,
                decision.Title ?? candidate.Title,
                decision.Body ?? candidate.Body,
                decision.Summary ?? candidate.Summary,
                candidate.Slug ?? match.Slug,
                candidate.Paths.Count > 0 ? candidate.Paths : match.Paths,
                candidate.Tags.Union(match.Tags).Distinct().ToList(),
                candidate.Authority,
                harness), cwd, ct);

            await _lore.SupersedeAsync(match.RepoId, match.Id, replacement.Id, ct);
            return new CaptureOutcome("supersede", replacement.Id, replacement.Title, decision.Reason);
        }

        // MERGE into existing coherent doc
        var mergedPaths = match.Paths.Union(candidate.Paths, StringComparer.OrdinalIgnoreCase).Distinct().ToList();
        var mergedTags = match.Tags.Union(candidate.Tags, StringComparer.OrdinalIgnoreCase).Distinct().ToList();
        var mergedAuthority = match.Authority == MemoryAuthority.User || candidate.Authority == MemoryAuthority.User
            ? MemoryAuthority.User
            : MemoryAuthority.Verified;

        var updated = await _lore.SaveAsync(new SaveMemoryRequest(
            match.RepoId,
            match.Type,
            decision.Title ?? match.Title,
            decision.Body ?? candidate.Body,
            decision.Summary ?? match.Summary,
            match.Slug ?? candidate.Slug,
            mergedPaths,
            mergedTags,
            mergedAuthority,
            harness,
            Id: match.Id), cwd, ct);

        return new CaptureOutcome("merge", updated.Id, updated.Title, decision.Reason);
    }

    private async Task<ReconcileDecision> DecideMergeOrSupersedeAsync(
        MemoryItem existing,
        ExtractionCandidate candidate,
        CancellationToken ct)
    {
        const string systemPrompt = """
            Compare an EXISTING memory against a NEW candidate discovery.
            Return ONLY valid JSON:
            {
              "action": "skip" | "merge" | "supersede",
              "reason": "short explanation",
              "title": "merged/new title if merge or supersede",
              "summary": "merged/new 1-2 sentence summary",
              "body": "complete updated Markdown document if merge or supersede"
            }
            Rules:
            - "skip": new candidate adds nothing meaningful over EXISTING.
            - "merge": new candidate adds steps, UI details, or clarifications to EXISTING; produce one clean, non-duplicative Markdown document preserving valid existing details.
            - "supersede": EXISTING is directly contradicted or obsolete.
            """;

        var userPrompt = $"""
            EXISTING ({existing.Type}, authority={existing.Authority}):
            Title: {existing.Title}
            Summary: {existing.Summary}
            Body:
            {existing.Body}

            NEW CANDIDATE ({candidate.Type}, authority={candidate.Authority}):
            Title: {candidate.Title}
            Summary: {candidate.Summary}
            Body:
            {candidate.Body}
            """;

        var raw = await _llm.CompleteAsync(systemPrompt, userPrompt, 2048, ct);
        try
        {
            var json = JObject.Parse(StripCodeFence(raw));
            var action = (json["action"]?.ToString() ?? "skip").ToLowerInvariant();
            if (action is not ("skip" or "merge" or "supersede"))
                action = "skip";

            return new ReconcileDecision(
                action,
                json["reason"]?.ToString() ?? string.Empty,
                json["title"]?.ToString(),
                json["summary"]?.ToString(),
                json["body"]?.ToString());
        }
        catch
        {
            return new ReconcileDecision("skip", "Failed to parse reconciliation JSON.", null, null, null);
        }
    }

    private static List<ExtractionCandidate> ParseCandidates(string raw)
    {
        try
        {
            var json = JObject.Parse(StripCodeFence(raw));
            var arr = json["candidates"] as JArray;
            if (arr is null) return [];

            var list = new List<ExtractionCandidate>();
            foreach (var item in arr.OfType<JObject>())
            {
                var title = item["title"]?.ToString();
                var body = item["body"]?.ToString();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
                    continue;

                var rawScope = item["scope"]?.ToString()?.Trim().ToLowerInvariant();
                var scope = rawScope == "global" ? "global" : "repo";

                list.Add(new ExtractionCandidate(
                    ParseType(item["type"]?.ToString()),
                    title.Trim(),
                    (item["summary"]?.ToString() ?? title).Trim(),
                    body.Trim(),
                    item["slug"]?.ToString()?.Trim(),
                    item["paths"]?.ToObject<List<string>>() ?? [],
                    item["tags"]?.ToObject<List<string>>() ?? [],
                    ParseAuthority(item["authority"]?.ToString()),
                    scope));
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    private static MemoryType ParseType(string? s) => s?.ToLowerInvariant() switch
    {
        "rule" => MemoryType.Rule,
        "process" => MemoryType.Process,
        "app_ui" => MemoryType.AppUi,
        _ => MemoryType.Note
    };

    private static MemoryAuthority ParseAuthority(string? s) => s?.ToLowerInvariant() switch
    {
        "user" => MemoryAuthority.User,
        "verified" => MemoryAuthority.Verified,
        _ => MemoryAuthority.Inferred
    };

    private static string StripCodeFence(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl >= 0) s = s[(firstNl + 1)..];
            if (s.EndsWith("```", StringComparison.Ordinal))
                s = s[..^3];
        }
        return s.Trim();
    }

    private sealed record ReconcileDecision(string Action, string Reason, string? Title, string? Summary, string? Body);
}
