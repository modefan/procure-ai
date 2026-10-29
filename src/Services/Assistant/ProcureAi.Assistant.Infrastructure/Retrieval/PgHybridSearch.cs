using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
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
/// <item>vector: <c>ORDER BY embedding &lt;=&gt; @vector</c> (cosine distance) on the partial HNSW index. Iterative index scans
/// (<c>hnsw.iterative_scan = strict_order</c>, pgvector 0.8) keep scanning until enough rows pass the filters, so a tenant with few
/// chunks among many other tenants' still gets its nearest ones;</item>
/// <item>full text: any of the query's terms (<see cref="AnyTermQuery"/>: <c>websearch_to_tsquery</c> with its ANDs turned into ORs),
/// ranked by <c>ts_rank_cd</c>, so a chunk matching more of the terms, closer together, ranks higher. With every term required, a natural
/// question ("Do I need finance approval for R 150 000 of software?") would match nothing. <c>websearch_to_tsquery</c> accepts any user
/// input (quotes, <c>-</c>, <c>OR</c>, stray operators) without a syntax error, and its output is valid <c>tsquery</c> text, so the cast
/// back cannot fail; an excluded term (<c>-word</c>) keeps its AND.</item>
/// </list>
/// Raw SQL is needed for <c>&lt;=&gt;</c> and the ranking, so both arms state the tenant (<see cref="AssistantDbContext.CurrentTenantId"/>)
/// and the searchable-chunk rules of <see cref="SearchableChunks"/> (ledger ruling B: purchase chunks always; policy chunks of the current
/// version of a document that was not removed) explicitly. The document check is an <c>EXISTS</c>, so the vector arm stays a scan of one
/// table in distance order, and the kind and source type literals are written into the SQL (not parameters), so the planner can match the
/// partial index (<c>kind = 'Child' AND embedding IS NOT NULL</c>). The arms are fused (<see cref="RrfMerger"/>, <c>RrfK</c>), reranked
/// (<see cref="IReranker"/>) and cut to the top K; the hits' texts, titles and parents are then read through EF Core, where the tenant
/// query filter applies once more.
/// </summary>
public sealed class PgHybridSearch(
    AssistantDbContext db, IEmbeddingGenerator<string, Embedding<float>> embeddings, IReranker reranker, IOptions<AssistantOptions> options)
    : IHybridSearch
{
    /// <summary>Any of the query's terms: <c>websearch_to_tsquery</c> with each <c>&amp;</c> made <c>|</c>, except before an excluded term.</summary>
    public const string AnyTermQuery =
        "regexp_replace(websearch_to_tsquery('english', @query)::text, ' & (?!!)', ' | ', 'g')::tsquery";

    /// <summary>What both arms may return (parameters <c>@tenant</c> and <c>@types</c>).</summary>
    private const string Searchable = """
        c.tenant_id = @tenant AND c.kind = 'Child' AND c.source_type = ANY(@types)
          AND (c.source_type <> 'Policy'
               OR EXISTS (SELECT 1 FROM policy_documents d
                          WHERE d.id = c.document_id AND d.tenant_id = @tenant AND NOT d.removed
                            AND d.current_version = c.document_version))
        """;

    internal const string NearestSql = $"""
        SELECT c.id AS "Value"
        FROM chunks c
        WHERE c.embedding IS NOT NULL AND {Searchable}
        ORDER BY c.embedding <=> @vector
        LIMIT @limit
        """;

    internal const string KeywordSql = $"""
        SELECT c.id AS "Value"
        FROM chunks c
        CROSS JOIN (SELECT {AnyTermQuery} AS query) AS q
        WHERE {Searchable} AND c.search @@ q.query
        ORDER BY ts_rank_cd(c.search, q.query) DESC, c.id
        LIMIT @limit
        """;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim() ?? "";
        if (db.CurrentTenantId.Length == 0 || query.Length == 0 || request.Types.Count == 0)
        {
            return [];
        }

        var retrieval = options.Value.Retrieval;
        var topK = request.TopK > 0 ? request.TopK : retrieval.TopK;
        var perArm = retrieval.CandidatesPerArm;
        var vector = await EmbedAsync(query, cancellationToken);

        var (nearest, keyword) = await InScanTransactionAsync(
            async () => (await RunAsync(NearestSql, Parameters(request.Types, perArm, vector: vector), cancellationToken),
                await RunAsync(KeywordSql, Parameters(request.Types, perArm, query: query), cancellationToken)),
            cancellationToken);
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

    /// <summary>The vector arm alone, nearest first (tests and diagnostics).</summary>
    internal async Task<List<Guid>> NearestArmAsync(string query, IReadOnlySet<SourceType> types, int limit, CancellationToken cancellationToken)
    {
        var vector = await EmbedAsync(query, cancellationToken);
        return await InScanTransactionAsync(
            () => RunAsync(NearestSql, Parameters(types, limit, vector: vector), cancellationToken), cancellationToken);
    }

    /// <summary>The full-text arm alone, best first (tests and diagnostics).</summary>
    internal Task<List<Guid>> KeywordArmAsync(string query, IReadOnlySet<SourceType> types, int limit, CancellationToken cancellationToken) =>
        InScanTransactionAsync(() => RunAsync(KeywordSql, Parameters(types, limit, query: query), cancellationToken), cancellationToken);

    /// <summary>The plan of the vector arm as the search runs it (<c>EXPLAIN</c>, with <c>ANALYZE</c> when asked).</summary>
    internal async Task<string> ExplainNearestAsync(
        string query, IReadOnlySet<SourceType> types, int limit, bool analyze, CancellationToken cancellationToken)
    {
        var vector = await EmbedAsync(query, cancellationToken);
        return await InScanTransactionAsync(async () =>
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = (analyze ? "EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF) " : "EXPLAIN (COSTS OFF) ") + NearestSql;
            command.Parameters.AddRange(Parameters(types, limit, vector: vector));
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }

            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);
    }

    private async Task<Vector> EmbedAsync(string query, CancellationToken cancellationToken) =>
        new((await embeddings.GenerateAsync([query], cancellationToken: cancellationToken))[0].Vector);

    private NpgsqlParameter[] Parameters(IReadOnlySet<SourceType> types, int limit, Vector? vector = null, string? query = null)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("tenant", db.CurrentTenantId),
            new("types", types.Select(t => t.ToString()).ToArray()),
            new("limit", limit),
        };
        if (vector is not null)
        {
            parameters.Add(new NpgsqlParameter("vector", vector));
        }

        if (query is not null)
        {
            parameters.Add(new NpgsqlParameter("query", query));
        }

        return [.. parameters];
    }

    private Task<List<Guid>> RunAsync(string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<Guid>(sql, parameters).ToListAsync(cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> with <c>hnsw.iterative_scan = strict_order</c>. <c>SET LOCAL</c> needs a transaction: our own, unless
    /// the caller already runs in one (it is then set for the rest of it, harmlessly).
    /// </summary>
    private async Task<T> InScanTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order", cancellationToken);
            var result = await work();
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return result;
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
