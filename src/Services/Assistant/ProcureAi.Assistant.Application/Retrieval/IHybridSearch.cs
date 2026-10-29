namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>
/// Hybrid retrieval over the caller tenant's searchable chunks (<see cref="SearchableChunks"/>): a vector arm (cosine distance to the
/// query's embedding) and a full-text arm (<c>websearch_to_tsquery</c>, ranked by <c>ts_rank_cd</c>), each limited to
/// <c>Assistant:Retrieval:CandidatesPerArm</c>, merged by reciprocal rank fusion (<see cref="RrfMerger"/>), optionally reranked
/// (<see cref="IReranker"/>), then cut to <see cref="SearchRequest.TopK"/>. Only child chunks are searched; each hit carries its parent
/// section's text for the prompt.
/// </summary>
public interface IHybridSearch
{
    /// <summary>The best hits for <paramref name="request"/>, best first; empty for a blank query or a caller without a tenant.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}
