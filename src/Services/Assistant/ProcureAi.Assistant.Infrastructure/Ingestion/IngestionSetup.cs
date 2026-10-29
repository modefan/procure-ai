using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ingestion;

namespace ProcureAi.Assistant.Infrastructure.Ingestion;

public static class IngestionSetup
{
    /// <summary>
    /// The text extractors (one per content type) and the chunk planner, sized by <c>Assistant:Chunking</c>. The <see cref="AssistantOptions"/>
    /// section itself is registered and validated at startup by <c>AddAssistantAi</c>.
    /// </summary>
    public static IServiceCollection AddAssistantIngestion(this IServiceCollection services)
    {
        services.AddSingleton<ITextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<ITextExtractor, MarkdownTextExtractor>();
        services.AddSingleton(sp => new ChunkPlanner(sp.GetRequiredService<IOptions<AssistantOptions>>().Value.Chunking));
        return services;
    }
}
