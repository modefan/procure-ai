using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Mongo;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace ProcureAi.Assistant.Infrastructure;

public static class DependencyInjection
{
    /// <summary>The main store: Postgres with pgvector (documents, chunks, purchases, Wolverine's outbox and inbox).</summary>
    public const string DatabaseConnectionName = "assistantdb";

    /// <summary>The second store: MongoDB for conversations and invoice scans (spec 5).</summary>
    public const string ChatDatabaseConnectionName = "assistantchatdb";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, pgvector enabled, audited), the chat database on MongoDB and the model providers
    /// and pipelines (<see cref="AiSetup.AddAssistantAi"/>). The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/>.
    /// </summary>
    public static IServiceCollection AddAssistantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<AssistantDbContext>(
            (sp, options) => options
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
                .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IAssistantDbContext>(sp => sp.GetRequiredService<AssistantDbContext>());
        services.TryAddSingleton(TimeProvider.System);

        services.AddAssistantChatDatabase(configuration, ChatDatabaseConnectionName);
        services.AddAssistantAi(configuration);
        return services;
    }

    /// <summary>
    /// Wolverine on the Postgres message store of <c>assistantdb</c> (durable inbox and outbox, EF Core transactions, the shared
    /// <c>assistant-api-access-changed</c> queue). The Assistant's own routes (past purchases, ingestion, progress events) come with the
    /// tasks that add them.
    /// </summary>
    public static void ConfigureAssistant(this WolverineOptions options, IConfiguration configuration, string serviceName) =>
        options.UseProcureAiMessaging<IAssistantDbContext, AssistantDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(AssistantOptions).Assembly);
}
