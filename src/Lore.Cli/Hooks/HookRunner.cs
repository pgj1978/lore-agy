using System.Diagnostics;
using System.Text;
using Lore.Cli.Cache;
using Lore.Cli.Client;
using Lore.Core.Config;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lore.Cli.Hooks;

public sealed class HookRunner
{
    private readonly ILoreClientFactory? _factory;
    private readonly ILoreClient? _fixedClient;
    private readonly GitStalenessChecker _git;
    private readonly LocalBootstrapCache? _fixedCache;
    private readonly LoreConfig? _fixedConfig;
    private readonly string? _cliExecutableOverride;

    public HookRunner(
        ILoreClientFactory factory,
        GitStalenessChecker git,
        string? cliExecutableOverride = null)
    {
        _factory = factory;
        _git = git;
        _cliExecutableOverride = cliExecutableOverride;
    }

    public HookRunner(
        ILoreClient client,
        GitStalenessChecker git,
        LocalBootstrapCache cache,
        LoreConfig config,
        string? cliExecutableOverride = null)
    {
        _fixedClient = client;
        _git = git;
        _fixedCache = cache;
        _fixedConfig = config;
        _cliExecutableOverride = cliExecutableOverride;
    }

    public async Task<string> HandleAsync(string harness, string eventName, string stdinJson, CancellationToken ct = default)
    {
        try
        {
            var input = ParseInput(stdinJson);
            var h = harness.ToLowerInvariant();
            var ev = eventName.ToLowerInvariant();

            LoreConfig config;
            ILoreClient client;
            LocalBootstrapCache cache;

            if (_factory is not null)
            {
                if (!_factory.TryResolve(input.Cwd, h, null, out var resolved, out _))
                    return EmptyOutput(h);

                config = resolved!.Config;
                client = resolved.Client;
                cache = resolved.Cache;
            }
            else
            {
                config = _fixedConfig!;
                if (!config.IsHarnessAllowed(h))
                    return EmptyOutput(h);

                client = _fixedClient!;
                cache = _fixedCache!;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Max(300, config.HookTimeoutMs));

            return ev switch
            {
                "session-start" => await HandleSessionStartAsync(h, input, client, cache, config, timeoutCts.Token),
                "prompt-push" => await HandlePromptPushAsync(h, input, client, cache, config, timeoutCts.Token),
                "stop" or "pre-compact" => HandleStopOrCompact(h, input, cache, config.ActiveProfileName),
                _ => EmptyOutput(h)
            };
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("LORE_DEBUG") == "1")
                Console.Error.WriteLine($"[lore-hook] {ex}");
            // Fail-open: hooks must never block or crash an agent turn
            return EmptyOutput(harness);
        }
    }

    private async Task<string> HandleSessionStartAsync(
        string harness, HookInput input, ILoreClient client, LocalBootstrapCache cache, LoreConfig config, CancellationToken ct)
    {
        var repoId = _git.ResolveRepoId(input.Cwd);
        var ttl = TimeSpan.FromMinutes(Math.Max(1, config.BootstrapCacheMinutes));
        var payload = cache.TryGetBootstrap(repoId, ttl);
        if (payload is null)
        {
            payload = await client.GetBootstrapAsync(repoId, ct);
            cache.SaveBootstrap(payload with { RepoId = repoId });
        }

        var message = FormatBootstrapMessage(payload, input.Cwd, _git);
        if (string.IsNullOrWhiteSpace(message))
            return EmptyOutput(harness);

        return WrapInjectedOutput(harness, message);
    }

    private async Task<string> HandlePromptPushAsync(
        string harness, HookInput input, ILoreClient client, LocalBootstrapCache cache, LoreConfig config, CancellationToken ct)
    {
        var prompt = input.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length < 15)
            return EmptyOutput(harness);

        var sessionId = string.IsNullOrWhiteSpace(input.SessionId) ? $"{harness}:{input.Cwd}" : input.SessionId;
        var state = cache.LoadSessionState(sessionId);
        var promptHash = LocalBootstrapCache.ComputeHash(prompt);

        // Deduplicate repeated PreInvocation calls within the same Agy tool-use loop
        if (string.Equals(state.LastPromptHash, promptHash, StringComparison.Ordinal))
            return EmptyOutput(harness);

        state.LastPromptHash = promptHash;

        var repoId = _git.ResolveRepoId(input.Cwd);
        var hits = await client.PromptRecallAsync(new PromptRecallRequest(
            repoId,
            prompt,
            state.InjectedMemoryIds.ToList(),
            config.PromptRecallMinScore,
            3,
            input.Cwd,
            harness), ct);

        if (hits.Count == 0)
        {
            cache.SaveSessionState(state);
            return EmptyOutput(harness);
        }

        foreach (var h in hits)
            state.InjectedMemoryIds.Add(h.MemoryId);

        cache.SaveSessionState(state);
        var message = FormatRecallMessage(hits);
        return WrapInjectedOutput(harness, message);
    }

    private string HandleStopOrCompact(string harness, HookInput input, LocalBootstrapCache cache, string profileName)
    {
        if (string.IsNullOrWhiteSpace(input.TranscriptPath) || !File.Exists(input.TranscriptPath))
            return EmptyOutput(harness);

        var sessionId = string.IsNullOrWhiteSpace(input.SessionId) ? $"{harness}:{input.TranscriptPath}" : input.SessionId;
        var state = cache.LoadSessionState(sessionId);

        var reader = TranscriptReaderFactory.ForHarness(harness);
        var entries = reader.Read(input.TranscriptPath, state.LastCapturedStep);
        if (entries.Count < 2)
            return EmptyOutput(harness);

        var maxStep = entries.Max(e => e.StepIndex);
        SpawnDetachedCapture(harness, input.TranscriptPath, input.Cwd, state.LastCapturedStep, sessionId, maxStep, profileName);
        return EmptyOutput(harness);
    }

    private void SpawnDetachedCapture(string harness, string transcriptPath, string cwd, int sinceStep, string sessionId, int maxStep, string profileName)
    {
        var exe = _cliExecutableOverride ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        var args = $"capture --harness \"{harness}\" --transcript \"{transcriptPath}\" --cwd \"{cwd}\" --profile \"{profileName}\" --since-step {sinceStep} --session-id \"{sessionId}\" --max-step {maxStep}";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var proc = Process.Start(psi);
        proc?.StandardInput.Close();
    }

    internal static string FormatBootstrapMessage(BootstrapPayload payload, string? cwd, GitStalenessChecker git)
    {
        if (payload.Rules.Count == 0 && payload.Toc.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<lore_memory>");
        sb.AppendLine($"Shared Lore memory for `{payload.RepoId}` (tools: `lore_search`, `lore_get`, `lore_save`, `lore_verify`):");

        if (payload.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Hard Rules (always apply)");
            foreach (var r in payload.Rules)
                sb.AppendLine($"- **{r.Title}** (`{r.Id}`): {r.Body.Replace("\r\n", " ").Replace('\n', ' ').Trim()}");
        }

        if (payload.Toc.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Known Processes & App/UI Specs (call `lore_get` before reinventing)");
            foreach (var entry in payload.Toc.Take(25))
            {
                var staleness = git.CheckStaleness(cwd, entry.VerifiedCommit, entry.Paths);
                var staleNote = staleness?.IsStale == true ? $" [possibly stale: {staleness.ChangedFileCount} file(s) changed]" : string.Empty;
                var typeTag = entry.Type == MemoryType.Process ? "process" : "app_ui";
                sb.AppendLine($"- [{typeTag}] **{entry.Title}** (`{entry.Id}`) — {entry.Summary}{staleNote}");
            }
        }

        sb.AppendLine("</lore_memory>");
        return sb.ToString().Trim();
    }

    internal static string FormatRecallMessage(IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<lore_memory>");
        sb.AppendLine("Relevant Lore memories for this prompt:");
        foreach (var h in hits)
        {
            var staleNote = h.Staleness?.IsStale == true ? $" [{h.Staleness.Message}]" : string.Empty;
            var typeTag = h.Type switch
            {
                MemoryType.Rule => "rule",
                MemoryType.Process => "process",
                MemoryType.AppUi => "app_ui",
                _ => "note"
            };
            sb.AppendLine($"- [{typeTag} | {h.Authority.ToString().ToLowerInvariant()} | score {h.VectorScore:F2}] **{h.Title}** (`{h.MemoryId}`){staleNote}: {h.Snippet}");
        }
        sb.AppendLine("Use `lore_get` for the complete process/app_ui document if needed.");
        sb.AppendLine("</lore_memory>");
        return sb.ToString().Trim();
    }

    private static string WrapInjectedOutput(string harness, string message)
    {
        if (harness == "agy")
        {
            return JsonConvert.SerializeObject(new
            {
                injectSteps = new[]
                {
                    new
                    {
                        systemMessage = new
                        {
                            systemMessage = message
                        }
                    }
                }
            });
        }

        // Claude Code & Codex CLI accept plain text on stdout (exit 0) for SessionStart / UserPromptSubmit
        return message;
    }

    private static string EmptyOutput(string harness)
        => harness.Equals("agy", StringComparison.OrdinalIgnoreCase) ? "{}" : string.Empty;

    internal static HookInput ParseInput(string stdinJson)
    {
        if (string.IsNullOrWhiteSpace(stdinJson))
            return new HookInput(string.Empty, Directory.GetCurrentDirectory(), string.Empty, null);

        try
        {
            var obj = JObject.Parse(stdinJson);
            var sessionId = obj["conversationId"]?.ToString()
                            ?? obj["session_id"]?.ToString()
                            ?? obj["sessionId"]?.ToString()
                            ?? string.Empty;

            var cwd = obj["cwd"]?.ToString()
                      ?? (obj["workspacePaths"] as JArray)?.FirstOrDefault()?.ToString()
                      ?? Directory.GetCurrentDirectory();

            var transcriptPath = obj["transcriptPath"]?.ToString()
                                 ?? obj["transcript_path"]?.ToString()
                                 ?? string.Empty;

            var prompt = obj["lastUserInput"]?.ToString()
                         ?? obj["prompt"]?.ToString()
                         ?? obj["user_prompt"]?.ToString();

            return new HookInput(sessionId, cwd, transcriptPath, prompt);
        }
        catch
        {
            return new HookInput(string.Empty, Directory.GetCurrentDirectory(), string.Empty, null);
        }
    }

    internal sealed record HookInput(string SessionId, string Cwd, string TranscriptPath, string? Prompt);
}
