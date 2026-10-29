using Microsoft.EntityFrameworkCore;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>The context and vector an earlier chunk with the same content already has.</summary>
public sealed record ReusableEmbedding(string? Context, float[] Embedding);

/// <summary>
/// Embedding reuse: a child chunk whose <c>(content_hash, embedding_model)</c> already exists in the tenant copies that chunk's context
/// and vector instead of calling the models again. Replacing a document with a version that changed one section embeds that section only.
/// Keyed on the kind too (children only): a parent shares its hash with the only child of a short section, but parents carry no vector.
/// </summary>
public static class EmbeddingReuse
{
    /// <summary>The newest stored embedding per hash of <paramref name="contentHashes"/> in the tenant, made by <paramref name="model"/>.</summary>
    public static async Task<IReadOnlyDictionary<string, ReusableEmbedding>> FindAsync(
        IAssistantDbContext db, string tenantId, IReadOnlyCollection<string> contentHashes, string model, CancellationToken cancellationToken)
    {
        if (contentHashes.Count == 0)
        {
            return new Dictionary<string, ReusableEmbedding>();
        }

        // The ingestion runs without a caller: the tenant filter is replaced by the tenant of the document.
        var found = await db.Chunks.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Kind == ChunkKind.Child && c.EmbeddingModel == model && c.Embedding != null
                && contentHashes.Contains(c.ContentHash))
            .Select(c => new { c.ContentHash, c.Context, c.Embedding, c.CreatedAt })
            .ToListAsync(cancellationToken);
        return found
            .GroupBy(c => c.ContentHash, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.CreatedAt).Select(c => new ReusableEmbedding(c.Context, c.Embedding!)).First(),
                StringComparer.Ordinal);
    }
}
