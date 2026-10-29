using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Persistence;

namespace ProcureAi.Assistant.Infrastructure.Retrieval;

/// <summary>
/// <see cref="IHybridSearch"/> on Postgres: two SQL arms over the caller tenant's child chunks, each limited to
/// <c>Assistant:Retrieval:CandidatesPerArm</c>:
/// <list type="bullet">
/// <item>vector: <c>ORDER BY embedding &lt;=&gt; @query</c> (cosine distance, HNSW index). Iterative index scans
/// (<c>hnsw.iterative_scan = strict_order</c>, pgvector 0.8) keep scanning until enough rows pass the filters, so a tenant with few
/// chunks among many other tenants' still gets its nearest ones;</item>
/// <item>full text: <c>search @@ websearch_to_tsquery('english', @query)</c> ranked by <c>ts_rank_cd</c> (GIN index).
/// <c>websearch_to_tsquery</c> accepts any user input (quotes, <c>-</c>, <c>OR</c>, stray operators) without a syntax error.</item>
/// </list>
/// Raw SQL is needed for <c>&lt;=&gt;</c> and the ranking, so both arms state the tenant (<see cref="AssistantDbContext.CurrentTenantId"/>)
/// and the searchable-chunk rules of <see cref="SearchableChunks"/> (ledger ruling B: purchase chunks always; policy chunks of the current
/// version of a document that was not removed) explicitly. The arms are fused (<see cref="RrfMerger"/>, <c>RrfK</c>), reranked
/// (<see cref="IReranker"/>) and cut to the top K; the hits' texts, titles and parents are then read through EF Core, where the tenant
/// query filter applies once more.
/// </summary>
public sealed class PgHybridSearch(
    AssistantDbContext db, IEmbeddingGenerator<string, Embedding<float>> embeddings, IReranker reranker, IOptions<AssistantOptions> options)
    : IHybridSearch
{
    private const string Child = nameof(ChunkKind.Child);
    private const string Policy = nameof(SourceType.Policy);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var tenant = db.CurrentTenantId;
        var query = request.Query?.Trim() ?? "";
        if (tenant.Length == 0 || query.Length == 0 || request.Types.Count == 0)
        {
            return [];
        }

        var retrieval = options.Value.Retrieval;
        var topK = request.TopK > 0 ? request.TopK : retrieval.TopK;
        var perArm = retrieval.CandidatesPerArm;
        var types = request.Types.Select(t => t.ToString()).ToArray();
        var vector = new Vector((await embeddings.GenerateAsync([query], cancellationToken: cancellationToken))[0].Vector);

        var (nearest, keyword) = await ArmsAsync(tenant, query, vector, types, perArm, cancellationToken);
        var fused = RrfMerger.Merge([nearest, keyword], retrieval.RrfK);
        // The reranker reads the fused candidates (up to one arm's worth); without it only the top K are loaded.
        var candidates = fused.Take(retrieval.Rerank ? Math.Max(topK, perArm) : topK).ToList();
        var hits = await LoadAsync(candidates, cancellationToken);
        if (retrieval.Rerank)
        {
            hits = await reranker.RerankAsync(query, hits, cancellationToken);
        }

        return [.. hits.Take(topK)];
    }

    private async Task<(List<Guid> Nearest, List<Guid> Keyword)> ArmsAsync(
        string tenant, string query, Vector vector, string[] types, int perArm, CancellationToken cancellationToken)
    {
        // SET LOCAL needs a transaction: our own, unless the caller already runs in one (it is then set for the rest of it, harmlessly).
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order", cancellationToken);
            var nearest = await db.Database.SqlQuery<Guid>($"""
                SELECT c.id AS "Value"
                FROM chunks c
                LEFT JOIN policy_documents d ON d.id = c.document_id
                WHERE c.tenant_id = {tenant} AND c.kind = {Child} AND c.embedding IS NOT NULL AND c.source_type = ANY({types})
                  AND (c.source_type <> {Policy}
                       OR (d.tenant_id = {tenant} AND NOT d.removed AND d.current_version = c.document_version))
                ORDER BY c.embedding <=> {vector}
                LIMIT {perArm}
                """).ToListAsync(cancellationToken);
            var keyword = await db.Database.SqlQuery<Guid>($"""
                SELECT c.id AS "Value"
                FROM chunks c
                LEFT JOIN policy_documents d ON d.id = c.document_id
                CROSS JOIN websearch_to_tsquery('english', {query}) AS q(query)
                WHERE c.tenant_id = {tenant} AND c.kind = {Child} AND c.source_type = ANY({types})
                  AND (c.source_type <> {Policy}
                       OR (d.tenant_id = {tenant} AND NOT d.removed AND d.current_version = c.document_version))
                  AND c.search @@ q.query
                ORDER BY ts_rank_cd(c.search, q.query) DESC, c.id
                LIMIT {perArm}
                """).ToListAsync(cancellationToken);
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return (nearest, keyword);
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    /// <summary>The hits of <paramref name="candidates"/>, in their order, read through the tenant query filter.</summary>
    private async Task<IReadOnlyList<SearchHit>> LoadAsync(IReadOnlyList<RrfResult<Guid>> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var ids = candidates.Select(c => c.Item).ToList();
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                c.SourceType,
                c.SectionPath,
                c.Text,
                ParentText = db.Chunks.Where(p => p.Id == c.ParentId).Select(p => p.Text).FirstOrDefault(),
                DocumentTitle = db.PolicyDocuments.Where(d => d.Id == c.DocumentId).Select(d => d.Title).FirstOrDefault(),
                Purchase = db.Purchases.Where(p => p.RequestId == c.PurchaseRequestId).Select(p => new { p.Title, p.Number }).FirstOrDefault(),
            })
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        var hits = new List<SearchHit>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!rows.TryGetValue(candidate.Item, out var row))
            {
                continue; // deleted since the arms ran (a version switch)
            }

            var isPurchase = row.SourceType == SourceType.Purchase;
            hits.Add(new SearchHit(
                row.Id,
                row.SourceType,
                (isPurchase ? row.Purchase?.Title : row.DocumentTitle) ?? "",
                row.SectionPath.Length == 0 ? [] : row.SectionPath.Split(Chunk.SectionSeparator),
                row.Text,
                row.ParentText ?? row.Text,
                isPurchase ? row.Purchase?.Number : null,
                candidate.Score));
        }

        return hits;
    }
}
