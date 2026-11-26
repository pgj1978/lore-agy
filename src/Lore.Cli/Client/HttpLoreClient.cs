using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;

namespace Lore.Cli.Client;

public sealed class HttpLoreClient : ILoreClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly HttpClient _http;
    private readonly GitStalenessChecker _git;

    public HttpLoreClient(HttpClient http, string baseUrl, string? apiKey, GitStalenessChecker git)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _git = git;
    }

    public async Task<BootstrapPayload> GetBootstrapAsync(string repoId, CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<BootstrapPayload>($"v1/bootstrap?repoId={Uri.EscapeDataString(repoId)}", JsonOpts, ct))!;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("v1/search", request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var hits = await resp.Content.ReadFromJsonAsync<List<SearchHit>>(JsonOpts, ct) ?? [];
        return EnrichStaleness(request.Cwd, hits);
    }

    public async Task<IReadOnlyList<SearchHit>> PromptRecallAsync(PromptRecallRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("v1/recall", request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var hits = await resp.Content.ReadFromJsonAsync<List<SearchHit>>(JsonOpts, ct) ?? [];
        return EnrichStaleness(request.Cwd, hits);
    }

    public async Task<MemoryItem?> GetAsync(string repoId, string id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"v1/memories/{Uri.EscapeDataString(id)}?repoId={Uri.EscapeDataString(repoId)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<MemoryItem>(JsonOpts, ct);
    }

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(string repoId, MemoryType? type = null, int limit = 50, CancellationToken ct = default)
    {
        var url = $"v1/memories?repoId={Uri.EscapeDataString(repoId)}&limit={limit}";
        if (type.HasValue) url += $"&type={type.Value.ToString().ToLowerInvariant()}";
        return await _http.GetFromJsonAsync<List<MemoryItem>>(url, JsonOpts, ct) ?? [];
    }

    public async Task<MemoryItem> SaveAsync(SaveMemoryRequest request, string? cwd = null, CancellationToken ct = default)
    {
        var enriched = request with { VerifiedCommit = request.VerifiedCommit ?? _git.GetHeadCommit(cwd) };
        using var resp = await _http.PostAsJsonAsync("v1/memories", enriched, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MemoryItem>(JsonOpts, ct))!;
    }

    public async Task<MemoryItem?> VerifyAsync(string repoId, string id, string? cwd = null, CancellationToken ct = default)
    {
        var commitSha = _git.GetHeadCommit(cwd);
        using var resp = await _http.PostAsJsonAsync($"v1/memories/{Uri.EscapeDataString(id)}/verify", new { repoId, commitSha }, JsonOpts, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<MemoryItem>(JsonOpts, ct);
    }

    public async Task ArchiveAsync(string repoId, string id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"v1/memories/{Uri.EscapeDataString(id)}?repoId={Uri.EscapeDataString(repoId)}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<StatsSummary> GetStatsAsync(string repoId, CancellationToken ct = default)
        => (await _http.GetFromJsonAsync<StatsSummary>($"v1/stats?repoId={Uri.EscapeDataString(repoId)}", JsonOpts, ct))!;

    public async Task<ExtractionReport> ExtractFromSlicesAsync(
        string repoId, string harness, string? cwd, IReadOnlyList<FilteredSlice> slices, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("v1/extract", new { repoId, harness, slices }, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ExtractionReport>(JsonOpts, ct))!;
    }

    private List<SearchHit> EnrichStaleness(string? cwd, List<SearchHit> hits)
        => hits.Select(h => h with { Staleness = _git.CheckStaleness(cwd, h.VerifiedCommit, h.Paths) }).ToList();
}
