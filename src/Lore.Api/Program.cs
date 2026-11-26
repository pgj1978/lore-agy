using System.Text.Json;
using System.Text.Json.Serialization;
using Lore.Core.Config;
using Lore.Core.Embeddings;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Models;
using Lore.Core.Services;
using Lore.Core.Storage;
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

var config = LoreConfig.Load();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<GitStalenessChecker>();
builder.Services.AddSingleton(_ =>
{
    var cred = LoreConfig.CreateAzureCredential();
    return new CosmosClient(config.CosmosEndpoint, cred);
});
builder.Services.AddSingleton<IMemoryStore>(sp =>
    new CosmosMemoryStore(sp.GetRequiredService<CosmosClient>(), config.CosmosDatabase));
builder.Services.AddSingleton<IEmbeddingProvider>(_ =>
    new AzureOpenAiEmbeddingProvider(
        config.AzureOpenAiEndpoint!,
        config.AzureOpenAiEmbeddingDeployment,
        LoreConfig.CreateAzureCredential(),
        config.EmbeddingDimensions));
builder.Services.AddSingleton<LoreService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    return new MemoryExtractor(
        new AnthropicLlmClient(http, config.AnthropicApiKey ?? string.Empty, config.AnthropicModel),
        sp.GetRequiredService<LoreService>());
});

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrWhiteSpace(config.ApiKey) && ctx.Request.Path != "/health")
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!string.Equals(auth, $"Bearer {config.ApiKey}", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "lore-api" }));

app.MapGet("/v1/doctor", async (
    LoreService lore,
    IEmbeddingProvider embeddings,
    IHttpClientFactory httpFactory,
    LoreConfig cfg,
    CancellationToken ct) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await lore.EnsureSchemaAsync(ct);
    var cosmosMs = sw.ElapsedMilliseconds;

    sw.Restart();
    var vec = await embeddings.EmbedAsync("lore health check", ct);
    var aoaiMs = sw.ElapsedMilliseconds;

    string anthropicStatus = "skipped";
    long anthropicMs = 0;
    if (!string.IsNullOrWhiteSpace(cfg.AnthropicApiKey))
    {
        sw.Restart();
        var llm = new AnthropicLlmClient(httpFactory.CreateClient(), cfg.AnthropicApiKey, cfg.AnthropicModel);
        var reply = await llm.CompleteAsync("Reply with OK only.", "ping", 16, ct);
        anthropicMs = sw.ElapsedMilliseconds;
        anthropicStatus = reply.Trim();
    }

    return Results.Ok(new
    {
        status = "ok",
        cosmosDatabase = cfg.CosmosDatabase,
        cosmosMs,
        embeddingDeployment = cfg.AzureOpenAiEmbeddingDeployment,
        embeddingDimensions = vec.Length,
        aoaiMs,
        anthropicModel = cfg.AnthropicModel,
        anthropicStatus,
        anthropicMs
    });
});

app.MapGet("/v1/bootstrap", async (string repoId, LoreService lore, CancellationToken ct) =>
    Results.Ok(await lore.GetBootstrapAsync(repoId, ct)));

app.MapPost("/v1/search", async (SearchRequest req, LoreService lore, CancellationToken ct) =>
    Results.Ok(await lore.SearchAsync(req, ct)));

app.MapPost("/v1/recall", async (PromptRecallRequest req, LoreService lore, CancellationToken ct) =>
    Results.Ok(await lore.PromptRecallAsync(req, ct)));

app.MapGet("/v1/memories", async (string repoId, MemoryType? type, int? limit, LoreService lore, CancellationToken ct) =>
    Results.Ok(await lore.ListAsync(repoId, type, limit ?? 50, ct)));

app.MapGet("/v1/memories/{id}", async (string id, string repoId, LoreService lore, CancellationToken ct) =>
    await lore.GetAsync(repoId, id, ct) is { } item ? Results.Ok(item) : Results.NotFound());

app.MapPost("/v1/memories", async (SaveMemoryRequest req, LoreService lore, CancellationToken ct) =>
    Results.Ok(await lore.SaveAsync(req, null, ct)));

app.MapPost("/v1/memories/{id}/verify", async (string id, VerifyRequest req, LoreService lore, CancellationToken ct) =>
    await lore.VerifyWithCommitAsync(req.RepoId, id, req.CommitSha, ct) is { } item ? Results.Ok(item) : Results.NotFound());

app.MapDelete("/v1/memories/{id}", async (string id, string repoId, LoreService lore, CancellationToken ct) =>
{
    await lore.ArchiveAsync(repoId, id, ct);
    return Results.NoContent();
});

app.MapPost("/v1/extract", async (ExtractSlicesRequest req, MemoryExtractor extractor, CancellationToken ct) =>
    Results.Ok(await extractor.ExtractAndStoreAsync(req.RepoId, req.Harness, null, req.Slices, ct)));

app.MapGet("/v1/stats", async (string repoId, LoreService lore, CancellationToken ct) =>
    Results.Ok(await lore.GetStatsAsync(repoId, ct)));

app.Run();

public sealed record VerifyRequest(string RepoId, string? CommitSha = null);
public sealed record ExtractSlicesRequest(string RepoId, string Harness, IReadOnlyList<FilteredSlice> Slices);
