using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Ingestion;
using Domain;
using Microsoft.Extensions.Options;

namespace Infrastructure.Llm;

/// <summary>
/// Embeddings from any OpenAI-compatible <c>POST /embeddings</c> endpoint (OpenAI, Azure OpenAI, many gateways
/// and local servers). Requests <see cref="DocumentChunk.EmbeddingDimensions"/>-dimensional vectors so the
/// existing column and index keep working. The HttpClient is configured with a Polly resilience pipeline
/// (retry with jitter that honours 429 Retry-After, circuit breaker, per-attempt and total timeouts) in DI.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingClient(HttpClient http, IOptions<EmbeddingsOptions> options) : IEmbeddingClient
{
    private OpenAiEmbeddingOptions Settings => options.Value.OpenAi;

    public string ModelId => $"openai:{Settings.Model}:{DocumentChunk.EmbeddingDimensions}";

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var all = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += Settings.BatchSize)
        {
            var batch = texts.Skip(offset).Take(Settings.BatchSize).ToList();
            all.AddRange(await EmbedBatchAsync(batch, cancellationToken));
        }

        return all;
    }

    private async Task<List<float[]>> EmbedBatchAsync(List<string> batch, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            "embeddings",
            new EmbeddingRequest(Settings.Model, batch, DocumentChunk.EmbeddingDimensions),
            ct);

        // 4xx other than the retryable 408/429 are our configuration (bad key/model): retrying cannot help.
        if ((int)response.StatusCode is >= 400 and < 500 and not (408 or 429))
        {
            throw IngestionException.Permanent($"The embeddings provider rejected the request ({(int)response.StatusCode}).");
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct)
            ?? throw new HttpRequestException("The embeddings provider returned an empty response.");

        if (body.Data.Count != batch.Count)
        {
            throw new HttpRequestException($"Expected {batch.Count} embeddings, got {body.Data.Count}.");
        }

        var ordered = body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
        if (ordered.Any(e => e.Length != DocumentChunk.EmbeddingDimensions))
        {
            throw IngestionException.Permanent(
                $"The embeddings provider returned vectors of the wrong size (expected {DocumentChunk.EmbeddingDimensions}). " +
                "Choose a model that supports the 'dimensions' parameter.");
        }

        return ordered;
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] List<string> Input,
        [property: JsonPropertyName("dimensions")] int Dimensions);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem> Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
