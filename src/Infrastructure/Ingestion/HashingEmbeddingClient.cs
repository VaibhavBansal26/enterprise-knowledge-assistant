using System.IO.Hashing;
using System.Text;
using Application.Ingestion;
using Domain;

namespace Infrastructure.Ingestion;

/// <summary>
/// A deterministic, offline embedding: words are hashed into a fixed-size vector (the "hashing trick"),
/// signed to reduce collisions, weighted by frequency, and L2-normalised. Texts that share vocabulary end up
/// close in cosine distance, which is enough to exercise the whole ingestion and search pipeline without a
/// network call or API key. It captures lexical overlap only, not meaning. Phase 5 swaps in a real model
/// behind the same <see cref="IEmbeddingClient"/> interface.
/// </summary>
public sealed class HashingEmbeddingClient : IEmbeddingClient
{
    private const int Dimensions = DocumentChunk.EmbeddingDimensions;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<float[]> result = texts.Select(Embed).ToList();
        return Task.FromResult(result);
    }

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        var token = new StringBuilder();

        void Flush()
        {
            if (token.Length == 0)
            {
                return;
            }

            var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(token.ToString()));
            var index = (int)(hash % Dimensions);
            vector[index] += (hash & 0x8000_0000) == 0 ? 1f : -1f;
            token.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        else
        {
            vector[0] = 1f; // keep the vector valid (non-zero) for cosine distance
        }

        return vector;
    }
}
