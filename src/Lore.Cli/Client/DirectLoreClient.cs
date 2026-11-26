using Lore.Core.Extraction;
using Lore.Core.Models;
using Lore.Core.Services;

namespace Lore.Cli.Client;

public sealed class DirectLoreClient : ILoreClient
{
    private readonly LoreService _service;
    private readonly Func<MemoryExtractor> _extractorFactory;

    public DirectLoreClient(LoreService service, Func<MemoryExtractor> extractorFactory)
    {
        _service = service;
        _extractorFactory = extractorFactory;
    }

    public Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default)
        => _service.GetBootstrapAsync(repoId, ct);

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        => _service.SearchAsync(request, ct);

    public Task<IReadOnlyList<SearchHit>> PromptRecallAsync(PromptRecallRequest request, CancellationToken ct = default)
        => _service.PromptRecallAsync(request, ct);

    public Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default)
        => _service.GetAsync(repoId, id, ct);

    public Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? type = null, int limit = 50, CancellationToken ct = default)
        => _service.ListAsync(repoId, type, limit, ct);

    public Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default)
        => _service.SaveAsync(request, cwd, ct);

    public Task<MemoryItem?> VerifyAsync(string repoId, string id, string? cwd = null, CancellationToken ct = default)
        => _service.VerifyAsync(repoId, id, cwd, ct);

    public Task ArchiveAsync(string repoId, string id, CancellationToken ct = default)
        => _service.ArchiveAsync(repoId, id, ct);

    public Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default)
        => _service.GetStatsAsync(repoId, ct);

    public Task<ExtractionReport> ExtractFromSlicesAsync(
        string repoId,
        string harness,
        string? cwd,
        IReadOnlyList<FilteredSlice> slices,
        CancellationToken ct = default)
        => _extractorFactory().ExtractAndStoreAsync(repoId, harness, cwd, slices, ct);
}
