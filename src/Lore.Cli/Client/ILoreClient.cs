using Lore.Core.Extraction;
using Lore.Core.Models;

namespace Lore.Cli.Client;

public interface ILoreClient
{
    Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SearchHit>> PromptRecallAsync(PromptRecallRequest request, CancellationToken ct = default);
    Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? type = null, int limit = 50, CancellationToken ct = default);
    Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default);
    Task<MemoryItem?> VerifyAsync(string repoId, string id, string? cwd = null, CancellationToken ct = default);
    Task ArchiveAsync(string repoId, string id, CancellationToken ct = default);
    Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default);
    Task<ExtractionReport> ExtractFromSlicesAsync(string repoId, string harness, string? cwd, IReadOnlyList<FilteredSlice> slices, CancellationToken ct = default);
}
