using System.Collections.Concurrent;
using Lore.Cli.Cache;
using Lore.Core.Config;
using Lore.Core.Embeddings;
using Lore.Core.Extraction;
using Lore.Core.Git;
using Lore.Core.Services;
using Lore.Core.Storage;
using Microsoft.Azure.Cosmos;

namespace Lore.Cli.Client;

public sealed record ResolvedProfile(
    LoreConfig Config,
    ILoreClient Client,
    LocalBootstrapCache Cache,
    LoreService? DirectService);

public sealed record McpHarnessContext(string? HarnessName, string? ExplicitProfile = null);

public interface ILoreClientFactory
{
    bool TryResolve(
        string? cwd,
        string? harness,
        string? explicitProfile,
        out ResolvedProfile? resolved,
        out string? deniedReason);

    LoreConfig InspectConfig(string? cwd, string? explicitProfile = null);
}

public sealed class LoreClientFactory : ILoreClientFactory, IDisposable
{
    private readonly GitStalenessChecker _git;
    private readonly HttpClient _http;
    private readonly bool _longLived;
    private readonly ConcurrentDictionary<string, ResolvedProfile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CosmosClient> _cosmosClients = [];

    public LoreClientFactory(GitStalenessChecker git, HttpClient http, bool longLived)
    {
        _git = git;
        _http = http;
        _longLived = longLived;
    }

    public LoreConfig InspectConfig(string? cwd, string? explicitProfile = null)
    {
        var mainPath = _git.ResolveWorktreeMainPath(cwd);
        return LoreConfig.LoadForDirectory(cwd, explicitProfile, mainPath);
    }

    public bool TryResolve(
        string? cwd,
        string? harness,
        string? explicitProfile,
        out ResolvedProfile? resolved,
        out string? deniedReason)
    {
        var config = InspectConfig(cwd, explicitProfile);
        if (!config.IsHarnessAllowed(harness))
        {
            resolved = null;
            deniedReason = $"Harness '{harness}' is blocked by Lore profile '{config.ActiveProfileName}'.";
            return false;
        }

        var cacheKey = $"{config.ActiveProfileName}|{config.ApiUrl}|{config.CosmosEndpoint}|{config.CosmosDatabase}";
        resolved = _cache.GetOrAdd(cacheKey, _ => BuildProfile(config));
        deniedReason = null;
        return true;
    }

    private ResolvedProfile BuildProfile(LoreConfig config)
    {
        var localCache = new LocalBootstrapCache(config.GetProfileDataDirectory());

        if (config.IsApiMode)
        {
            var httpClient = new HttpLoreClient(_http, config.ApiUrl!, config.ApiKey, _git);
            return new ResolvedProfile(config, httpClient, localCache, null);
        }

        var cred = LoreConfig.CreateAzureCredential();
        var opts = new CosmosClientOptions
        {
            ConnectionMode = _longLived ? ConnectionMode.Direct : ConnectionMode.Gateway
        };
        var cosmos = new CosmosClient(config.CosmosEndpoint, cred, opts);
        lock (_cosmosClients) _cosmosClients.Add(cosmos);

        var store = new CosmosMemoryStore(cosmos, config.CosmosDatabase);
        var embeddings = new AzureOpenAiEmbeddingProvider(
            config.AzureOpenAiEndpoint ?? throw new InvalidOperationException($"AzureOpenAiEndpoint is not configured for profile '{config.ActiveProfileName}'."),
            config.AzureOpenAiEmbeddingDeployment,
            cred,
            config.EmbeddingDimensions);
        var service = new LoreService(store, embeddings, _git, config);

        MemoryExtractor ExtractorFactory() => new(
            new AnthropicLlmClient(
                _http,
                config.AnthropicApiKey ?? throw new InvalidOperationException($"ANTHROPIC_API_KEY is not configured for profile '{config.ActiveProfileName}'."),
                config.AnthropicModel),
            service);

        return new ResolvedProfile(config, new DirectLoreClient(service, ExtractorFactory), localCache, service);
    }

    public void Dispose()
    {
        lock (_cosmosClients)
        {
            foreach (var c in _cosmosClients)
                c.Dispose();
            _cosmosClients.Clear();
        }
    }
}
