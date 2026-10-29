namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>Reorders fused candidates by relevance to the query. <see cref="NoReranker"/> by default; <see cref="ModelReranker"/> when <c>Assistant:Retrieval:Rerank</c>.</summary>
public interface IReranker
{
    /// <summary>The same hits, best first. Never drops or adds a hit.</summary>
    Task<IReadOnlyList<SearchHit>> RerankAsync(string query, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken);
}
