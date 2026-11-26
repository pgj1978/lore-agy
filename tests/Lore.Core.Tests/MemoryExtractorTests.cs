using Lore.Core.Extraction;
using Lore.Core.Models;
using Lore.Core.Services;
using Xunit;

namespace Lore.Core.Tests;

public sealed class MemoryExtractorTests
{
    [Fact]
    public async Task SearchBeforeWrite_MergesIntoExistingProcess()
    {
        var existing = new MemoryItem
        {
            Id = "mem_proc_1",
            RepoId = "repo1",
            Type = MemoryType.Process,
            Slug = "proc/local-login",
            Title = "Local Login Steps",
            Summary = "Seed test user and sign in.",
            Body = "## Steps\n1. Run seed script.",
            Authority = MemoryAuthority.Verified
        };

        var fakeLore = new FakeLoreOps([existing]);
        var fakeLlm = new QueueLlmClient(
            """
            {
              "candidates": [
                {
                  "type": "process",
                  "title": "Local Login Steps",
                  "summary": "Seed test user and pass dev header.",
                  "body": "## Steps\n2. Pass X-Dev-User header.",
                  "slug": "proc/local-login",
                  "paths": ["src/Auth/**"],
                  "tags": ["auth"],
                  "authority": "verified"
                }
              ]
            }
            """,
            """
            {
              "action": "merge",
              "reason": "Adds missing step 2",
              "title": "Local Login Steps",
              "summary": "Seed test user and pass X-Dev-User header.",
              "body": "## Steps\n1. Run seed script.\n2. Pass X-Dev-User header."
            }
            """);

        var extractor = new MemoryExtractor(fakeLlm, fakeLore);
        var report = await extractor.ExtractAndStoreAsync(
            "repo1", "agy", null, [new FilteredSlice("error_recovery", 1, 3, "fixed login")]);

        Assert.Single(report.Outcomes);
        Assert.Equal("merge", report.Outcomes[0].Action);
        Assert.Equal("mem_proc_1", report.Outcomes[0].MemoryId);
        Assert.Contains("2. Pass X-Dev-User header", fakeLore.Items["mem_proc_1"].Body);
    }

    [Fact]
    public async Task SearchBeforeWrite_ProtectsUserRuleFromInferredOverwrite()
    {
        var userRule = new MemoryItem
        {
            Id = "mem_rule_1",
            RepoId = "repo1",
            Type = MemoryType.Rule,
            Slug = "rule/no-rg-prefix",
            Title = "Never prefix RG with rg",
            Body = "Resource groups must not start with rg.",
            Authority = MemoryAuthority.User
        };

        var fakeLore = new FakeLoreOps([userRule]);
        var fakeLlm = new QueueLlmClient(
            """
            {
              "candidates": [
                {
                  "type": "rule",
                  "title": "Never prefix RG with rg",
                  "summary": "Always prefix with rg",
                  "body": "Use rg- prefix.",
                  "slug": "rule/no-rg-prefix",
                  "authority": "inferred"
                }
              ]
            }
            """);

        var extractor = new MemoryExtractor(fakeLlm, fakeLore);
        var report = await extractor.ExtractAndStoreAsync(
            "repo1", "codex", null, [new FilteredSlice("session_outcome", 1, 2, "something")]);

        Assert.Single(report.Outcomes);
        Assert.Equal("skip", report.Outcomes[0].Action);
        Assert.Equal("Resource groups must not start with rg.", fakeLore.Items["mem_rule_1"].Body);
    }

    [Fact]
    public async Task SearchBeforeWrite_SupersedesOutdatedNote()
    {
        var oldNote = new MemoryItem
        {
            Id = "mem_old",
            RepoId = "repo1",
            Type = MemoryType.Note,
            Title = "API runs on port 5000",
            Summary = "Port 5000",
            Body = "Dev API listens on 5000.",
            Authority = MemoryAuthority.Verified
        };

        var fakeLore = new FakeLoreOps([oldNote]) { SimulatedSearchScore = 0.85 };
        var fakeLlm = new QueueLlmClient(
            """
            {
              "candidates": [
                {
                  "type": "note",
                  "title": "API runs on port 5100",
                  "summary": "Moved from 5000 to 5100",
                  "body": "Dev API now listens on 5100.",
                  "authority": "verified"
                }
              ]
            }
            """,
            """
            {
              "action": "supersede",
              "reason": "Port changed from 5000 to 5100",
              "title": "API runs on port 5100",
              "summary": "Moved from 5000 to 5100",
              "body": "Dev API now listens on 5100."
            }
            """);

        var extractor = new MemoryExtractor(fakeLlm, fakeLore);
        var report = await extractor.ExtractAndStoreAsync(
            "repo1", "claude", null, [new FilteredSlice("error_recovery", 1, 4, "port 5100")]);

        Assert.Equal("supersede", report.Outcomes[0].Action);
        Assert.Equal(MemoryStatus.Superseded, fakeLore.Items["mem_old"].Status);
        Assert.NotNull(fakeLore.Items["mem_old"].SupersededBy);
    }

    private sealed class QueueLlmClient(params string[] responses) : ILlmClient
    {
        private readonly Queue<string> _queue = new(responses);

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2048, CancellationToken ct = default)
            => Task.FromResult(_queue.Dequeue());
    }

    private sealed class FakeLoreOps : ILoreStoreOperations
    {
        public Dictionary<string, MemoryItem> Items { get; } = new();
        public double SimulatedSearchScore { get; set; } = 0.8;

        public FakeLoreOps(IEnumerable<MemoryItem> seed)
        {
            foreach (var i in seed) Items[i.Id] = i;
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            var hits = Items.Values
                .Where(m => m.Status == MemoryStatus.Active)
                .Take(request.Limit)
                .Select(m => new SearchHit(m.Id, m.RepoId, m.Type, m.Slug, m.Title, m.Title, m.Summary, m.Authority, SimulatedSearchScore, m.Paths, m.VerifiedCommit, m.UpdatedAt))
                .ToList();
            return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
        }

        public Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default)
            => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<MemoryItem?> FindBySlugAsync(string repoId, string slug, CancellationToken ct = default)
            => Task.FromResult(Items.Values.FirstOrDefault(m => m.Slug == slug && m.Status == MemoryStatus.Active));

        public Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default)
        {
            var item = request.Id is not null && Items.TryGetValue(request.Id, out var existing)
                ? existing
                : new MemoryItem { RepoId = request.RepoId };

            item.Type = request.Type;
            item.Title = request.Title;
            item.Summary = request.Summary ?? request.Title;
            item.Body = request.Body;
            item.Slug = request.Slug ?? item.Slug;
            item.Authority = request.Authority;
            item.Status = MemoryStatus.Active;
            Items[item.Id] = item;
            return Task.FromResult(item);
        }

        public Task SupersedeAsync(string repoId, string oldId, string newId, CancellationToken ct = default)
        {
            if (Items.TryGetValue(oldId, out var old))
            {
                old.Status = MemoryStatus.Superseded;
                old.SupersededBy = newId;
            }
            return Task.CompletedTask;
        }

        public Task LogCaptureOutcomeAsync(string repoId, string harness, string action, string detail, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
