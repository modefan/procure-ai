using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Infrastructure.Ai.Fake;

/// <summary>
/// A deterministic embedding with lexical meaning (ruling 15): the lowercase word tokens and word bigrams of the text, feature-hashed
/// (FNV-1a, 32 bit) into <see cref="Chunk.EmbeddingDimensions"/> buckets with a sign taken from the hash, then L2-normalised. Texts
/// that share words get a high cosine similarity, so retrieval and its tests behave like a real (if simple) model, with no key.
/// </summary>
public sealed partial class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>The one token of a text without words, so the vector is never all zeros (cosine is undefined there).</summary>
    private const string EmptyToken = "\u0000empty";

    private readonly EmbeddingGeneratorMetadata _metadata =
        new("fake", null, AiOptions.FakeEmbeddingModel, Chunk.EmbeddingDimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        long tokens = 0;
        foreach (var value in values)
        {
            var words = Words(value);
            tokens += words.Count;
            embeddings.Add(new Embedding<float>(Embed(words))
            {
                ModelId = AiOptions.FakeEmbeddingModel,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        embeddings.Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens };
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    /// <summary>The vector of a text (exposed for tests).</summary>
    public static float[] Embed(string text) => Embed(Words(text));

    private static float[] Embed(List<string> words)
    {
        var vector = new float[Chunk.EmbeddingDimensions];
        if (words.Count == 0)
        {
            Add(vector, EmptyToken);
        }

        for (var i = 0; i < words.Count; i++)
        {
            Add(vector, words[i]);
            if (i + 1 < words.Count)
            {
                Add(vector, words[i] + " " + words[i + 1]);
            }
        }

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    private static void Add(float[] vector, string feature)
    {
        var hash = Fnv1a(feature);
        // The low bits pick the bucket, the top bit the sign, so colliding features tend to cancel instead of piling up.
        var bucket = (int)(hash % (uint)vector.Length);
        vector[bucket] += (hash & 0x8000_0000u) == 0 ? 1f : -1f;
    }

    private static uint Fnv1a(string text)
    {
        var hash = FnvOffset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static List<string> Words(string text) =>
        [.. WordPattern().Matches(text ?? "").Select(m => m.Value.ToLowerInvariant())];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
