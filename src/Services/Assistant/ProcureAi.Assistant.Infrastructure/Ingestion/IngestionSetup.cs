using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Guardrails;
using ProcureAi.Assistant.Infrastructure.Persistence;

namespace ProcureAi.Assistant.Infrastructure.Ingestion;

public static class IngestionSetup
{
    /// <summary>
    /// The text extractors (one per content type), the chunk planner (sized by <c>Assistant:Chunking</c>), the document guard (the
    /// allow-all placeholder unless one is registered first; plan 05 Task 4), the context writer, the ingestion job and its units of work.
    /// The <see cref="AssistantOptions"/> section itself is registered and validated at startup by <c>AddAssistantAi</c>.
    /// </summary>
    public static IServiceCollection AddAssistantIngestion(this IServiceCollection services)
    {
        services.AddSingleton<ITextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<ITextExtractor, MarkdownTextExtractor>();
        services.AddSingleton(sp => new ChunkPlanner(sp.GetRequiredService<IOptions<AssistantOptions>>().Value.Chunking));
        services.TryAddSingleton<IPromptShield, AllowAllPromptShield>();
        services.TryAddSingleton<IAiFailures, AiFailures>();
        services.AddSingleton<ContextWriter>();
        services.AddSingleton<IAssistantUnitOfWorkFactory, AssistantUnitOfWorkFactory>();
        services.AddScoped<PolicyIngestion>();
        return services;
    }
}
