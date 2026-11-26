using System.Diagnostics;
using Lore.Core.Config;
using Lore.Core.Embeddings;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;
using Lore.Core.Storage;

namespace Lore.Core.Services;

public interface ILoreStoreOperations
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default);
    Task<MemoryItem?> FindBySlugAsync(string repoId, string slug, CancellationToken ct = default);
    Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default);
    Task SupersedeAsync(string repoId, string oldId, string newId, CancellationToken ct = default);
    Task LogCaptureOutcomeAsync(string repoId, string harness, string action, string detail, CancellationToken ct = default);
}

public sealed class LoreService : ILoreStoreOperations
{
    private readonly IMemoryStore _store;
    private readonly IEmbeddingProvider _embeddings;
    private readonly GitStalenessChecker _git;
    private readonly LoreConfig _config;

    public LoreService(
        IMemoryStore store,
        IEmbeddingProvider embeddings,
        GitStalenessChecker git,
        LoreConfig config)
    {
        _store = store;
        _embeddings = embeddings;
        _git = git;
        _config = config;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var vector = await _embeddings.EmbedAsync(request.Query, ct);
        var hits = await _store.HybridSearchAsync(request.RepoId, request.Query, vector, request.Type, request.Limit, ct);
        sw.Stop();

        var enriched = hits.Select(h =>
        {
            var staleness = _git.CheckStaleness(request.Cwd, h.VerifiedCommit, h.Paths);
            return h with { Staleness = staleness };
        }).ToList();

        await TryLogAsync(new RetrievalLog
        {
            RepoId = request.RepoId,
            EventType = "search",
            Harness = request.Harness,
            LatencyMs = sw.ElapsedMilliseconds,
            HitCount = enriched.Count,
            TopScore = enriched.Count > 0 ? enriched[0].VectorScore : null,
            Injected = false,
            Detail = request.Query.Length > 120 ? request.Query[..120] : request.Query
        }, ct);

        return enriched;
    }

    public async Task<IReadOnlyList<SearchHit>> PromptRecallAsync(PromptRecallRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var minScore = request.MinScore ?? _config.PromptRecallMinScore;
        var exclude = request.AlreadyInjectedIds?.ToHashSet(StringComparer.Ordinal) ?? [];

        var vector = await _embeddings.EmbedAsync(request.Prompt, ct);
        var hits = await _store.HybridSearchAsync(request.RepoId, request.Prompt, vector, null, request.Limit + exclude.Count, ct);
        sw.Stop();

        var filtered = hits
            .Where(h => h.VectorScore >= minScore && !exclude.Contains(h.MemoryId))
            .Take(request.Limit)
            .Select(h => h with { Staleness = _git.CheckStaleness(request.Cwd, h.VerifiedCommit, h.Paths) })
            .ToList();

        await TryLogAsync(new RetrievalLog
        {
            RepoId = request.RepoId,
            EventType = "prompt_push",
            Harness = request.Harness,
            LatencyMs = sw.ElapsedMilliseconds,
            HitCount = filtered.Count,
            TopScore = hits.Count > 0 ? hits[0].VectorScore : null,
            Injected = filtered.Count > 0
        }, ct);

        return filtered;
    }

    public Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default)
        => _store.GetAsync(repoId, id, ct);

    public Task<MemoryItem?> FindBySlugAsync(string repoId, string slug, CancellationToken ct = default)
        => _store.FindBySlugAsync(repoId, slug, ct);

    public Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? type = null, int limit = 50, CancellationToken ct = default)
        => _store.ListAsync(repoId, type, limit, ct);

    public Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default)
        => _store.GetBootstrapAsync(repoId, ct);

    public async Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default)
    {
        MemoryItem? existing = null;
        if (!string.IsNullOrWhiteSpace(request.Id))
            existing = await _store.GetAsync(request.RepoId, request.Id, ct);
        else if (!string.IsNullOrWhiteSpace(request.Slug))
            existing = await _store.FindBySlugAsync(request.RepoId, request.Slug, ct);

        var now = DateTimeOffset.UtcNow;
        var commit = request.VerifiedCommit ?? _git.GetHeadCommit(cwd);

        var item = existing ?? new MemoryItem
        {
            RepoId = request.RepoId,
            CreatedAt = now,
            CreatedBy = request.CreatedBy ?? _config.UserName ?? Environment.UserName
        };

        item.Type = request.Type;
        item.Title = request.Title.Trim();
        item.Summary = string.IsNullOrWhiteSpace(request.Summary) ? item.Title : request.Summary.Trim();
        item.Body = request.Body.Trim();
        item.Slug = string.IsNullOrWhiteSpace(request.Slug) ? item.Slug : request.Slug.Trim();
        item.Paths = request.Paths ?? item.Paths;
        item.Tags = request.Tags ?? item.Tags;
        item.Authority = request.Authority;
        item.Status = MemoryStatus.Active;
        item.SourceHarness = request.SourceHarness;
        item.UpdatedAt = now;
        item.VerifiedCommit = commit ?? item.VerifiedCommit;
        if (request.Authority != MemoryAuthority.Inferred)
            item.VerifiedAt = now;

        var specs = MarkdownChunker.Chunk(item);
        var vectors = await _embeddings.EmbedBatchAsync(specs.Select(s => s.SearchText).ToList(), ct);

        var chunks = specs.Select((spec, idx) => new MemoryChunk
        {
            Id = $"{item.Id}_c{spec.Index}",
            MemoryId = item.Id,
            RepoId = item.RepoId,
            ChunkIndex = spec.Index,
            Type = item.Type,
            Slug = item.Slug,
            Title = item.Title,
            SectionHeading = spec.Heading,
            Snippet = spec.Snippet,
            SearchText = spec.SearchText,
            Embedding = vectors[idx],
            Authority = item.Authority,
            Status = item.Status,
            Paths = item.Paths,
            VerifiedCommit = item.VerifiedCommit,
            UpdatedAt = item.UpdatedAt
        }).ToList();

        await _store.UpsertWithChunksAsync(item, chunks, ct);
        return item;
    }

    public Task<MemoryItem?> VerifyAsync(string repoId, string id, string? cwd = null, CancellationToken ct = default)
        => _store.VerifyAsync(repoId, id, _git.GetHeadCommit(cwd), ct);

    public Task<MemoryItem?> VerifyWithCommitAsync(string repoId, string id, string? commitSha, CancellationToken ct = default)
        => _store.VerifyAsync(repoId, id, commitSha, ct);

    public Task ArchiveAsync(string repoId, string id, CancellationToken ct = default)
        => _store.UpdateStatusAsync(repoId, id, MemoryStatus.Archived, null, ct);

    public Task SupersedeAsync(string repoId, string oldId, string newId, CancellationToken ct = default)
        => _store.UpdateStatusAsync(repoId, oldId, MemoryStatus.Superseded, newId, ct);

    public Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default)
        => _store.GetStatsAsync(repoId, ct);

    public Task EnsureSchemaAsync(CancellationToken ct = default)
        => _store.EnsureSchemaAsync(_config.EmbeddingDimensions, ct);

    public Task LogCaptureOutcomeAsync(string repoId, string harness, string action, string detail, CancellationToken ct = default)
        => TryLogAsync(new RetrievalLog
        {
            RepoId = repoId,
            EventType = $"capture_{action}",
            Harness = harness,
            Detail = detail
        }, ct);

    private async Task TryLogAsync(RetrievalLog log, CancellationToken ct)
    {
        try
        {
            await _store.LogEventAsync(log, ct);
        }
        catch
        {
            // Telemetry is best-effort and must never break search or hooks
        }
    }
}
