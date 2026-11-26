using Lore.Core.Models;

namespace Lore.Core.Storage;

public interface IMemoryStore
{
    Task EnsureSchemaAsync(int embeddingDimensions = 1536, CancellationToken ct = default);

    Task<IReadOnlyList<SearchHit>> HybridSearchAsync(
        string repoId,
        string queryText,
        float[] queryVector,
        MemoryType? typeFilter = null,
        int limit = 5,
        CancellationToken ct = default);

    Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default);
    Task<MemoryItem?> FindBySlugAsync(string repoId, string slug, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? typeFilter = null, int limit = 50, CancellationToken ct = default);
    Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default);
    Task UpsertWithChunksAsync(MemoryItem item, IReadOnlyList<MemoryChunk> chunks, CancellationToken ct = default);
    Task UpdateStatusAsync(string repoId, string id, MemoryStatus status, string? supersededBy = null, CancellationToken ct = default);
    Task<MemoryItem?> VerifyAsync(string repoId, string id, string? commitSha, CancellationToken ct = default);
    Task LogEventAsync(RetrievalLog log, CancellationToken ct = default);
    Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default);
}
