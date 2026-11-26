using Lore.Cli.Cache;
using Lore.Cli.Client;
using Lore.Cli.Hooks;
using Lore.Core.Config;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;
using Xunit;

namespace Lore.Core.Tests;

public sealed class HookRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public HookRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lore-hook-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task AgyPromptPush_DeduplicatesSamePrompt_AndSkipsTrivial()
    {
        var client = new StubLoreClient
        {
            RecallHits =
            [
                new SearchHit("mem_1", "repo", MemoryType.Process, "proc/login", "Login Process", "Steps", "Use dev token", MemoryAuthority.Verified, 0.81, [], null, DateTimeOffset.UtcNow)
            ]
        };
        var runner = new HookRunner(client, new GitStalenessChecker(), new LocalBootstrapCache(_tempDir), new LoreConfig());

        var payload = $$"""{"conversationId":"conv-1","lastUserInput":"How do I log in locally to test the API?","cwd":"{{_tempDir.Replace("\\", "\\\\")}}"}""";

        // First call injects
        var first = await runner.HandleAsync("agy", "prompt-push", payload);
        Assert.Contains("Login Process", first);
        Assert.Equal(1, client.RecallCalls);

        // Second PreInvocation inside same tool loop with unchanged lastUserInput skips immediately
        var second = await runner.HandleAsync("agy", "prompt-push", payload);
        Assert.Equal("{}", second);
        Assert.Equal(1, client.RecallCalls);

        // Trivial prompt skips without calling search
        var trivialPayload = $$"""{"conversationId":"conv-1","lastUserInput":"ok thanks","cwd":"{{_tempDir.Replace("\\", "\\\\")}}"}""";
        var third = await runner.HandleAsync("agy", "prompt-push", trivialPayload);
        Assert.Equal("{}", third);
        Assert.Equal(1, client.RecallCalls);
    }

    [Fact]
    public async Task PromptPush_FailsOpenWhenBackendThrows()
    {
        var client = new StubLoreClient { ThrowOnRecall = true };
        var runner = new HookRunner(client, new GitStalenessChecker(), new LocalBootstrapCache(_tempDir), new LoreConfig());

        var payload = """{"conversationId":"conv-2","lastUserInput":"Explain how invoices approval works"}""";
        var agyOut = await runner.HandleAsync("agy", "prompt-push", payload);
        var claudeOut = await runner.HandleAsync("claude", "prompt-push", """{"session_id":"c2","prompt":"Explain how invoices approval works"}""");

        Assert.Equal("{}", agyOut);
        Assert.Equal(string.Empty, claudeOut);
    }

    [Fact]
    public async Task SessionStart_UsesBootstrapCache()
    {
        var client = new StubLoreClient
        {
            Bootstrap = new BootstrapPayload(
                "local:test",
                DateTimeOffset.UtcNow,
                [new MemoryItem { Id = "mem_r1", Type = MemoryType.Rule, Title = "No rg prefix", Body = "Never start RG names with rg." }],
                [new BootstrapTocEntry("mem_p1", "local:test", MemoryType.Process, "proc/deploy", "Deploy Flow", "ACR + Container Apps", [], null)])
        };
        var runner = new HookRunner(client, new GitStalenessChecker(), new LocalBootstrapCache(_tempDir), new LoreConfig());

        var input = $$"""{"conversationId":"conv-3","cwd":"{{_tempDir.Replace("\\", "\\\\")}}"}""";
        var out1 = await runner.HandleAsync("agy", "session-start", input);
        var out2 = await runner.HandleAsync("claude", "session-start", input);

        Assert.Contains("No rg prefix", out1);
        Assert.Contains("Deploy Flow", out1);
        Assert.Contains("No rg prefix", out2);
        Assert.Equal(1, client.BootstrapCalls);
    }

    private sealed class StubLoreClient : ILoreClient
    {
        public int RecallCalls { get; private set; }
        public int BootstrapCalls { get; private set; }
        public bool ThrowOnRecall { get; set; }
        public List<SearchHit> RecallHits { get; set; } = [];
        public BootstrapPayload Bootstrap { get; set; } = new("_global", DateTimeOffset.UtcNow, [], []);

        public Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default)
        {
            BootstrapCalls++;
            return Task.FromResult(Bootstrap);
        }

        public Task<IReadOnlyList<SearchHit>> PromptRecallAsync(PromptRecallRequest request, CancellationToken ct = default)
        {
            RecallCalls++;
            if (ThrowOnRecall) throw new InvalidOperationException("Simulated outage");
            return Task.FromResult<IReadOnlyList<SearchHit>>(RecallHits);
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SearchHit>>([]);
        public Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default) => Task.FromResult<MemoryItem?>(null);
        public Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? type = null, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MemoryItem>>([]);
        public Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MemoryItem?> VerifyAsync(string repoId, string id, string? cwd = null, CancellationToken ct = default) => Task.FromResult<MemoryItem?>(null);
        public Task ArchiveAsync(string repoId, string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ExtractionReport> ExtractFromSlicesAsync(string repoId, string harness, string? cwd, IReadOnlyList<FilteredSlice> slices, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
