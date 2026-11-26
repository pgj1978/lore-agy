using Azure.AI.OpenAI;
using Azure.Core;
using OpenAI.Embeddings;

namespace Lore.Core.Embeddings;

public sealed class AzureOpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;

    public AzureOpenAiEmbeddingProvider(string endpoint, string deploymentName, TokenCredential credential, int dimensions = 1536)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("AzureOpenAiEndpoint is required.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(deploymentName))
            throw new ArgumentException("AzureOpenAiEmbeddingDeployment is required.", nameof(deploymentName));

        Dimensions = dimensions;
        var aoaiClient = new AzureOpenAIClient(new Uri(endpoint), credential);
        _client = aoaiClient.GetEmbeddingClient(deploymentName);
    }

    public int Dimensions { get; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var options = new EmbeddingGenerationOptions { Dimensions = Dimensions };
        var response = await _client.GenerateEmbeddingAsync(text, options, ct);
        return response.Value.ToFloats().ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];

        var options = new EmbeddingGenerationOptions { Dimensions = Dimensions };
        var response = await _client.GenerateEmbeddingsAsync(texts, options, ct);
        return response.Value
            .OrderBy(e => e.Index)
            .Select(e => e.ToFloats().ToArray())
            .ToList();
    }
}
