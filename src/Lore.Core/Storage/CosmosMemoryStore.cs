using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using Lore.Core.Models;
using Microsoft.Azure.Cosmos;

namespace Lore.Core.Storage;

public sealed partial class CosmosMemoryStore : IMemoryStore
{
    private const string MemoriesContainerId = "memories";
    private const string ChunksContainerId = "chunks";
    private const string TelemetryContainerId = "telemetry";

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "are", "was", "were",
        "how", "what", "why", "when", "where", "does", "did", "can", "will", "not",
        "you", "your", "into", "about", "have", "has", "had", "but", "they", "their"
    };

    private readonly CosmosClient _client;
    private readonly string _databaseId;

    public CosmosMemoryStore(CosmosClient client, string databaseId = "lore")
    {
        _client = client;
        _databaseId = databaseId;
    }

    private Container Memories => _client.GetContainer(_databaseId, MemoriesContainerId);
    private Container Chunks => _client.GetContainer(_databaseId, ChunksContainerId);
    private Container Telemetry => _client.GetContainer(_databaseId, TelemetryContainerId);

    public async Task EnsureSchemaAsync(int embeddingDimensions = 1536, CancellationToken ct = default)
    {
        // Fast path: if database & containers were already provisioned via control plane (ARM / setup-azure.ps1),
        // data-plane Entra ID RBAC allows reading container metadata without needing control-plane POST /dbs.
        try
        {
            await Memories.ReadContainerAsync(cancellationToken: ct);
            await Chunks.ReadContainerAsync(cancellationToken: ct);
            await Telemetry.ReadContainerAsync(cancellationToken: ct);
            return;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Fall through to create if authorized (e.g. emulator or key auth)
        }

        var dbResponse = await _client.CreateDatabaseIfNotExistsAsync(_databaseId, cancellationToken: ct);
        var db = dbResponse.Database;

        await db.CreateContainerIfNotExistsAsync(
            new ContainerProperties(MemoriesContainerId, "/repoId"),
            cancellationToken: ct);

        var chunkProps = new ContainerProperties(ChunksContainerId, "/repoId")
        {
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
            {
                new()
                {
                    Path = "/embedding",
                    DataType = VectorDataType.Float32,
                    DistanceFunction = DistanceFunction.Cosine,
                    Dimensions = embeddingDimensions
                }
            }),
            FullTextPolicy = new FullTextPolicy
            {
                DefaultLanguage = "en-US",
                FullTextPaths = new Collection<FullTextPath>
                {
                    new()
                    {
                        Path = "/searchText",
                        Language = "en-US"
                    }
                }
            },
            IndexingPolicy = new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent
            }
        };

        chunkProps.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/embedding/*" });
        chunkProps.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
        {
            Path = "/embedding",
            Type = VectorIndexType.DiskANN
        });
        chunkProps.IndexingPolicy.FullTextIndexes.Add(new FullTextIndexPath
        {
            Path = "/searchText"
        });

        await db.CreateContainerIfNotExistsAsync(chunkProps, cancellationToken: ct);

        var telemetryProps = new ContainerProperties(TelemetryContainerId, "/repoId")
        {
            DefaultTimeToLive = 90 * 86400
        };
        await db.CreateContainerIfNotExistsAsync(telemetryProps, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<SearchHit>> HybridSearchAsync(
        string repoId,
        string queryText,
        float[] queryVector,
        MemoryType? typeFilter = null,
        int limit = 5,
        CancellationToken ct = default)
    {
        var topK = Math.Clamp(limit * 3, 6, 30);
        var terms = ExtractSearchTerms(queryText);

        var whereClause = "WHERE c.repoId IN (@repoId, '_global') AND c.status = 'active'";
        if (typeFilter.HasValue)
            whereClause += " AND c.type = @type";

        string orderByClause;
        if (terms.Count > 0)
        {
            var termParams = string.Join(", ", terms.Select((_, i) => $"@t{i}"));
            orderByClause = $"ORDER BY RANK RRF(VectorDistance(c.embedding, @queryVector), FullTextScore(c.searchText, {termParams}), [2, 1])";
        }
        else
        {
            orderByClause = "ORDER BY VectorDistance(c.embedding, @queryVector)";
        }

        var sql = $"""
            SELECT TOP @k
                c.memoryId,
                c.repoId,
                c.type,
                c.slug,
                c.title,
                c.sectionHeading,
                c.snippet,
                c.authority,
                c.paths,
                c.verifiedCommit,
                c.updatedAt,
                VectorDistance(c.embedding, @queryVector) AS vectorScore
            FROM c
            {whereClause}
            {orderByClause}
            """;

        var qd = new QueryDefinition(sql)
            .WithParameter("@k", topK)
            .WithParameter("@repoId", repoId)
            .WithParameter("@queryVector", queryVector);

        if (typeFilter.HasValue)
            qd.WithParameter("@type", ToWireValue(typeFilter.Value));

        for (var i = 0; i < terms.Count; i++)
            qd.WithParameter($"@t{i}", terms[i]);

        var results = new List<SearchHit>();
        var seenMemories = new HashSet<string>(StringComparer.Ordinal);

        using var iter = Chunks.GetItemQueryIterator<ChunkHitRow>(qd);
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            foreach (var row in page)
            {
                if (!seenMemories.Add(row.MemoryId))
                    continue;

                results.Add(new SearchHit(
                    row.MemoryId,
                    row.RepoId,
                    row.Type,
                    row.Slug,
                    row.Title,
                    row.SectionHeading,
                    row.Snippet,
                    row.Authority,
                    row.VectorScore,
                    row.Paths ?? [],
                    row.VerifiedCommit,
                    row.UpdatedAt));

                if (results.Count >= limit)
                    return results;
            }
        }

        return results;
    }

    public async Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default)
    {
        var item = await TryReadMemoryAsync(repoId, id, ct);
        if (item is not null || string.Equals(repoId, "_global", StringComparison.Ordinal))
            return item;

        return await TryReadMemoryAsync("_global", id, ct);
    }

    public async Task<MemoryItem?> FindBySlugAsync(string repoId, string slug, CancellationToken ct = default)
    {
        var qd = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.repoId = @repoId AND c.slug = @slug AND c.status = 'active'")
            .WithParameter("@repoId", repoId)
            .WithParameter("@slug", slug);

        using var iter = Memories.GetItemQueryIterator<MemoryItem>(
            qd, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(repoId) });

        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            var first = page.FirstOrDefault();
            if (first is not null)
                return first;
        }

        return null;
    }

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(
        string repoId,
        MemoryType? typeFilter = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var sql = "SELECT TOP @limit * FROM c WHERE c.repoId IN (@repoId, '_global') AND c.status = 'active'";
        if (typeFilter.HasValue)
            sql += " AND c.type = @type";
        sql += " ORDER BY c.updatedAt DESC";

        var qd = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@repoId", repoId);

        if (typeFilter.HasValue)
            qd.WithParameter("@type", ToWireValue(typeFilter.Value));

        var list = new List<MemoryItem>();
        using var iter = Memories.GetItemQueryIterator<MemoryItem>(qd);
        while (iter.HasMoreResults)
        {
            var page = await iter.ReadNextAsync(ct);
            list.AddRange(page);
        }

        return list;
    }

    public async Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default)
    {
        var sql = """
            SELECT * FROM c
            WHERE c.repoId IN (@repoId, '_global')
              AND c.status = 'active'
              AND c.type IN ('rule', 'process', 'app_ui')
            ORDER BY c.updatedAt DESC
            """;

        var rules = new List<MemoryItem>();
        var toc = new List<BootstrapTocEntry>();

        using var iter = Memories.GetItemQueryIterator<MemoryItem>(
            new QueryDefinition(sql).WithParameter("@repoId", repoId));
        while (iter.HasMoreResults)
        {
            foreach (var m in await iter.ReadNextAsync(ct))
            {
                if (m.Type == MemoryType.Rule)
                    rules.Add(m);
                else
                    toc.Add(new BootstrapTocEntry(m.Id, m.RepoId, m.Type, m.Slug, m.Title, m.Summary, m.Paths, m.VerifiedCommit));
            }
        }

        return new BootstrapPayload(repoId, DateTimeOffset.UtcNow, rules, toc);
    }

    public async Task UpsertWithChunksAsync(
        MemoryItem item,
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken ct = default)
    {
        await Memories.UpsertItemAsync(item, new PartitionKey(item.RepoId), cancellationToken: ct);
        await DeleteChunksForMemoryAsync(item.RepoId, item.Id, ct);

        foreach (var chunk in chunks)
            await Chunks.UpsertItemAsync(chunk, new PartitionKey(chunk.RepoId), cancellationToken: ct);
    }

    public async Task UpdateStatusAsync(
        string repoId,
        string id,
        MemoryStatus status,
        string? supersededBy = null,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(repoId, id, ct);
        if (existing is null)
            return;

        existing.Status = status;
        existing.SupersededBy = supersededBy ?? existing.SupersededBy;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await Memories.UpsertItemAsync(existing, new PartitionKey(existing.RepoId), cancellationToken: ct);

        var chunkIds = await GetChunkIdsForMemoryAsync(existing.RepoId, existing.Id, ct);
        foreach (var chunkId in chunkIds)
        {
            await Chunks.PatchItemAsync<MemoryChunk>(
                chunkId,
                new PartitionKey(existing.RepoId),
                [PatchOperation.Replace("/status", ToWireValue(status))],
                cancellationToken: ct);
        }
    }

    public async Task<MemoryItem?> VerifyAsync(string repoId, string id, string? commitSha, CancellationToken ct = default)
    {
        var existing = await GetAsync(repoId, id, ct);
        if (existing is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        existing.Authority = existing.Authority == MemoryAuthority.Inferred
            ? MemoryAuthority.Verified
            : existing.Authority;
        existing.VerifiedAt = now;
        existing.VerifiedCommit = commitSha ?? existing.VerifiedCommit;
        existing.UpdatedAt = now;

        await Memories.UpsertItemAsync(existing, new PartitionKey(existing.RepoId), cancellationToken: ct);

        var chunkIds = await GetChunkIdsForMemoryAsync(existing.RepoId, existing.Id, ct);
        foreach (var chunkId in chunkIds)
        {
            var ops = new List<PatchOperation>
            {
                PatchOperation.Replace("/authority", ToWireValue(existing.Authority)),
                PatchOperation.Replace("/updatedAt", now)
            };
            if (!string.IsNullOrWhiteSpace(existing.VerifiedCommit))
                ops.Add(PatchOperation.Set("/verifiedCommit", existing.VerifiedCommit));

            await Chunks.PatchItemAsync<MemoryChunk>(
                chunkId, new PartitionKey(existing.RepoId), ops, cancellationToken: ct);
        }

        return existing;
    }

    public async Task LogEventAsync(RetrievalLog log, CancellationToken ct = default)
    {
        await Telemetry.CreateItemAsync(log, new PartitionKey(log.RepoId), cancellationToken: ct);
    }

    public async Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default)
    {
        var memories = await ListAsync(repoId, null, 500, ct);
        var byType = memories
            .GroupBy(m => ToWireValue(m.Type))
            .ToDictionary(g => g.Key, g => g.Count());

        var logs = new List<RetrievalLog>();
        var qd = new QueryDefinition(
            "SELECT TOP 500 * FROM c WHERE c.repoId IN (@repoId, '_global') ORDER BY c.timestamp DESC")
            .WithParameter("@repoId", repoId);

        using var iter = Telemetry.GetItemQueryIterator<RetrievalLog>(qd);
        while (iter.HasMoreResults)
            logs.AddRange(await iter.ReadNextAsync(ct));

        var searches = logs.Where(l => l.EventType == "search").ToList();
        var pushes = logs.Where(l => l.EventType == "prompt_push").ToList();
        var captures = logs.Where(l => l.EventType.StartsWith("capture_", StringComparison.Ordinal)).ToList();

        return new StatsSummary(
            repoId,
            memories.Count,
            byType,
            searches.Count,
            pushes.Count(p => p.Injected),
            pushes.Count(p => !p.Injected),
            searches.Count > 0 ? Math.Round(searches.Average(s => s.LatencyMs), 1) : 0,
            captures.Count(c => c.EventType == "capture_create"),
            captures.Count(c => c.EventType == "capture_merge"),
            captures.Count(c => c.EventType == "capture_supersede"),
            captures.Count(c => c.EventType == "capture_skip"));
    }

    private async Task<MemoryItem?> TryReadMemoryAsync(string repoId, string id, CancellationToken ct)
    {
        try
        {
            var resp = await Memories.ReadItemAsync<MemoryItem>(id, new PartitionKey(repoId), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task DeleteChunksForMemoryAsync(string repoId, string memoryId, CancellationToken ct)
    {
        var ids = await GetChunkIdsForMemoryAsync(repoId, memoryId, ct);
        foreach (var chunkId in ids)
        {
            try
            {
                await Chunks.DeleteItemAsync<MemoryChunk>(chunkId, new PartitionKey(repoId), cancellationToken: ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
    }

    private async Task<List<string>> GetChunkIdsForMemoryAsync(string repoId, string memoryId, CancellationToken ct)
    {
        var qd = new QueryDefinition("SELECT VALUE c.id FROM c WHERE c.repoId = @repoId AND c.memoryId = @memoryId")
            .WithParameter("@repoId", repoId)
            .WithParameter("@memoryId", memoryId);

        var ids = new List<string>();
        using var iter = Chunks.GetItemQueryIterator<string>(
            qd, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(repoId) });

        while (iter.HasMoreResults)
            ids.AddRange(await iter.ReadNextAsync(ct));

        return ids;
    }

    private static List<string> ExtractSearchTerms(string queryText)
    {
        var matches = WordRegex().Matches(queryText);
        var terms = new List<string>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in matches)
        {
            var word = m.Value;
            if (word.Length < 2 || StopWords.Contains(word) || !seen.Add(word))
                continue;

            terms.Add(word.ToLowerInvariant());
            if (terms.Count >= 8)
                break;
        }

        return terms;
    }

    private static string ToWireValue(MemoryType t) => t switch
    {
        MemoryType.Rule => "rule",
        MemoryType.Process => "process",
        MemoryType.AppUi => "app_ui",
        MemoryType.Note => "note",
        _ => "note"
    };

    private static string ToWireValue(MemoryAuthority a) => a switch
    {
        MemoryAuthority.User => "user",
        MemoryAuthority.Verified => "verified",
        MemoryAuthority.Inferred => "inferred",
        _ => "verified"
    };

    private static string ToWireValue(MemoryStatus s) => s switch
    {
        MemoryStatus.Active => "active",
        MemoryStatus.Superseded => "superseded",
        MemoryStatus.Archived => "archived",
        _ => "active"
    };

    [GeneratedRegex(@"[A-Za-z0-9_./-]{2,}", RegexOptions.Compiled)]
    private static partial Regex WordRegex();

    private sealed class ChunkHitRow
    {
        public string MemoryId { get; set; } = string.Empty;
        public string RepoId { get; set; } = string.Empty;
        public MemoryType Type { get; set; }
        public string? Slug { get; set; }
        public string Title { get; set; } = string.Empty;
        public string SectionHeading { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public MemoryAuthority Authority { get; set; }
        public List<string>? Paths { get; set; }
        public string? VerifiedCommit { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public double VectorScore { get; set; }
    }
}
