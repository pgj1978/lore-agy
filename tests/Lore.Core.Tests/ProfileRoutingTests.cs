using Lore.Cli.Cache;
using Lore.Cli.Client;
using Lore.Cli.Hooks;
using Lore.Core.Config;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;
using Lore.Core.Services;
using Xunit;

namespace Lore.Core.Tests;

public sealed class ProfileRoutingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _prevConfigEnv;
    private readonly string? _prevSecretsEnv;
    private readonly string? _prevDataEnv;

    public ProfileRoutingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lore-prof-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _prevConfigEnv = Environment.GetEnvironmentVariable("LORE_CONFIG");
        _prevSecretsEnv = Environment.GetEnvironmentVariable("LORE_SECRETS");
        _prevDataEnv = Environment.GetEnvironmentVariable("LORE_DATA_DIR");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LORE_CONFIG", _prevConfigEnv);
        Environment.SetEnvironmentVariable("LORE_SECRETS", _prevSecretsEnv);
        Environment.SetEnvironmentVariable("LORE_DATA_DIR", _prevDataEnv);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void LegacyFlatConfig_LoadsAsDefaultProfile()
    {
        var cfgPath = Path.Combine(_tempDir, "flat.json");
        File.WriteAllText(cfgPath, """
            {
              "cosmosEndpoint": "https://personal.documents.azure.com:443/",
              "cosmosDatabase": "lore",
              "azureOpenAiEndpoint": "https://personal.openai.azure.com/"
            }
            """);
        Environment.SetEnvironmentVariable("LORE_CONFIG", cfgPath);
        Environment.SetEnvironmentVariable("LORE_SECRETS", Path.Combine(_tempDir, "none.json"));

        var loaded = LoreConfig.LoadForDirectory(@"D:\code\some-repo");
        Assert.Equal("default", loaded.ActiveProfileName);
        Assert.Equal("lore", loaded.CosmosDatabase);
        Assert.True(loaded.IsHarnessAllowed("agy"));
        Assert.True(loaded.IsHarnessAllowed("claude"));
    }

    [Fact]
    public void PathRoutes_MatchesLongestBoundarySafePrefix_AndIsolatesCacheAndHarnesses()
    {
        var cfgPath = Path.Combine(_tempDir, "profiles.json");
        var dataDir = Path.Combine(_tempDir, "data");
        var clientDir = Path.Combine(_tempDir, "work", "ClientA");
        var siblingDir = Path.Combine(_tempDir, "work", "ClientAlpha");
        var nestedRepo = Path.Combine(clientDir, "Api");
        Directory.CreateDirectory(nestedRepo);
        Directory.CreateDirectory(siblingDir);

        File.WriteAllText(cfgPath, $$"""
            {
              "defaultProfile": "personal",
              "profiles": {
                "personal": {
                  "cosmosEndpoint": "https://personal.documents.azure.com:443/",
                  "cosmosDatabase": "lore"
                },
                "clientA": {
                  "cosmosEndpoint": "https://clienta.documents.azure.com:443/",
                  "cosmosDatabase": "lore-clienta",
                  "allowedHarnesses": ["claude", "codex"]
                }
              },
              "pathRoutes": {
                "{{clientDir.Replace("\\", "\\\\")}}": "clientA"
              }
            }
            """);

        Environment.SetEnvironmentVariable("LORE_CONFIG", cfgPath);
        Environment.SetEnvironmentVariable("LORE_SECRETS", Path.Combine(_tempDir, "none.json"));
        Environment.SetEnvironmentVariable("LORE_DATA_DIR", dataDir);

        var clientCfg = LoreConfig.LoadForDirectory(nestedRepo);
        Assert.Equal("clientA", clientCfg.ActiveProfileName);
        Assert.Equal("lore-clienta", clientCfg.CosmosDatabase);
        Assert.False(clientCfg.IsHarnessAllowed("agy"));
        Assert.True(clientCfg.IsHarnessAllowed("claude"));
        Assert.True(clientCfg.IsHarnessAllowed("claude-code"));
        Assert.True(clientCfg.IsHarnessAllowed("codex"));

        // Sibling path "ClientAlpha" must NOT match "ClientA" prefix
        var siblingCfg = LoreConfig.LoadForDirectory(siblingDir);
        Assert.Equal("personal", siblingCfg.ActiveProfileName);
        Assert.Equal("lore", siblingCfg.CosmosDatabase);
        Assert.True(siblingCfg.IsHarnessAllowed("agy"));

        // Profile local cache directories are physically separate
        Assert.NotEqual(clientCfg.GetProfileDataDirectory(), siblingCfg.GetProfileDataDirectory());
        Assert.EndsWith(Path.Combine("profiles", "clienta"), clientCfg.GetProfileDataDirectory());
    }

    [Fact]
    public async Task HookRunner_FailsClosedForBlockedHarness()
    {
        var factory = new BlockingStubFactory(allowedHarness: "claude");
        var runner = new HookRunner(factory, new GitStalenessChecker());

        var agyResult = await runner.HandleAsync("agy", "session-start", """{"conversationId":"c1","cwd":"D:\\tmp"}""");
        var claudeResult = await runner.HandleAsync("claude", "session-start", """{"session_id":"c2","cwd":"D:\\tmp"}""");

        Assert.Equal("{}", agyResult);
        Assert.Contains("Client Rule", claudeResult);
        Assert.Equal(1, factory.BootstrapCalls);
    }

    [Fact]
    public async Task Extractor_RoutesGlobalScopeCandidatesToGlobalPartition()
    {
        var store = new RecordingLoreOps();
        var llm = new SingleResponseLlm("""
            {
              "candidates": [
                {
                  "type": "rule",
                  "scope": "global",
                  "title": "Never run docker system prune -a",
                  "summary": "Shared containers live in WSL.",
                  "body": "Ask user before pruning Docker.",
                  "authority": "user"
                },
                {
                  "type": "note",
                  "scope": "repo",
                  "title": "Local API uses port 5100",
                  "summary": "Port 5100",
                  "body": "Configured in launchSettings.json",
                  "authority": "verified"
                }
              ]
            }
            """);

        var extractor = new MemoryExtractor(llm, store);
        var report = await extractor.ExtractAndStoreAsync(
            "github.com/org/app",
            "claude",
            null,
            [new FilteredSlice("user_input", 0, 1, "rule + note")]);

        Assert.Equal(2, report.Outcomes.Count);
        Assert.Contains(store.Saved, s => s.Title == "Never run docker system prune -a" && s.RepoId == "_global");
        Assert.Contains(store.Saved, s => s.Title == "Local API uses port 5100" && s.RepoId == "github.com/org/app");
    }

    [Fact]
    public void GitStalenessChecker_ResolveScope_HandlesNonRepoAreasAndGlobal()
    {
        var git = new GitStalenessChecker();
        Assert.Equal("_global", git.ResolveScope(_tempDir, "global"));
        Assert.Equal("_global", git.ResolveScope(_tempDir, "_global"));
        Assert.Equal("area:homelab", git.ResolveScope(_tempDir, "area:Homelab"));
        Assert.Equal("area:deploy-standards", git.ResolveScope(_tempDir, "deploy-standards"));
        Assert.StartsWith("local:", git.ResolveScope(_tempDir, "repo"));
    }

    private sealed class BlockingStubFactory(string allowedHarness) : ILoreClientFactory
    {
        public int BootstrapCalls { get; private set; }

        public LoreConfig InspectConfig(string? cwd, string? explicitProfile = null)
            => new() { ActiveProfileName = "client", AllowedHarnesses = [allowedHarness] };

        public bool TryResolve(string? cwd, string? harness, string? explicitProfile, out ResolvedProfile? resolved, out string? deniedReason)
        {
            var cfg = InspectConfig(cwd, explicitProfile);
            if (!cfg.IsHarnessAllowed(harness))
            {
                resolved = null;
                deniedReason = "blocked";
                return false;
            }

            var stub = new StubClient(() => BootstrapCalls++);
            var tempCache = new LocalBootstrapCache(Path.Combine(Path.GetTempPath(), $"lore-stub-{Guid.NewGuid():N}"));
            resolved = new ResolvedProfile(cfg, stub, tempCache, null);
            deniedReason = null;
            return true;
        }
    }

    private sealed class StubClient(Action onBootstrap) : ILoreClient
    {
        public Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default)
        {
            onBootstrap();
            return Task.FromResult(new BootstrapPayload(
                repoId,
                DateTimeOffset.UtcNow,
                [new MemoryItem { Id = "r1", Type = MemoryType.Rule, Title = "Client Rule", Body = "Keep separated." }],
                []));
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SearchHit>>([]);
        public Task<IReadOnlyList<SearchHit>> PromptRecallAsync(PromptRecallRequest request, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SearchHit>>([]);
        public Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default) => Task.FromResult<MemoryItem?>(null);
        public Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? type = null, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryItem>>([]);
        public Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MemoryItem?> VerifyAsync(string repoId, string id, string? cwd = null, CancellationToken ct = default) => Task.FromResult<MemoryItem?>(null);
        public Task ArchiveAsync(string repoId, string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExtractionReport> ExtractFromSlicesAsync(string repoId, string harness, string? cwd, IReadOnlyList<FilteredSlice> slices, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class SingleResponseLlm(string response) : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2048, CancellationToken ct = default)
            => Task.FromResult(response);
    }

    private sealed class RecordingLoreOps : ILoreStoreOperations
    {
        public List<MemoryItem> Saved { get; } = [];
        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SearchHit>>([]);
        public Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default) => Task.FromResult<MemoryItem?>(null);
        public Task<MemoryItem?> FindBySlugAsync(string repoId, string slug, CancellationToken ct = default) => Task.FromResult<MemoryItem?>(null);

        public Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default)
        {
            var item = new MemoryItem
            {
                RepoId = request.RepoId,
                Type = request.Type,
                Title = request.Title,
                Summary = request.Summary ?? request.Title,
                Body = request.Body,
                Authority = request.Authority
            };
            Saved.Add(item);
            return Task.FromResult(item);
        }

        public Task SupersedeAsync(string repoId, string oldId, string newId, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogCaptureOutcomeAsync(string repoId, string harness, string action, string detail, CancellationToken ct = default) => Task.CompletedTask;
    }
}
