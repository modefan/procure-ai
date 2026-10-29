using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;

namespace ProcureAi.Assistant.Infrastructure.Retrieval;

public static class RetrievalSetup
{
    /// <summary>
    /// Hybrid search (<see cref="IHybridSearch"/>, scoped: it reads through the request's DbContext and its tenant), the reranker
    /// (<see cref="ModelReranker"/> when <c>Assistant:Retrieval:Rerank</c>, else <see cref="NoReranker"/>) and the past purchases indexer.
    /// </summary>
    public static IServiceCollection AddAssistantRetrieval(this IServiceCollection services)
    {
        services.AddScoped<IHybridSearch, PgHybridSearch>();
        services.AddSingleton<NoReranker>();
        services.AddSingleton<ModelReranker>();
        services.AddSingleton<IReranker>(sp => sp.GetRequiredService<IOptions<AssistantOptions>>().Value.Retrieval.Rerank
            ? sp.GetRequiredService<ModelReranker>()
            : sp.GetRequiredService<NoReranker>());
        services.AddScoped<PurchaseIndexer>();
        return services;
    }
}
