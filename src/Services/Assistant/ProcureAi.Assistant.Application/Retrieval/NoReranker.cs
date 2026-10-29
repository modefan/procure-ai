namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>Keeps the fusion's order (the default: no extra model call per question).</summary>
public sealed class NoReranker : IReranker
{
    public Task<IReadOnlyList<SearchHit>> RerankAsync(string query, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken) =>
        Task.FromResult(hits);
}
