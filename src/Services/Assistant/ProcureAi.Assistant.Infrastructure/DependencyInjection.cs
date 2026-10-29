using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Ingestion;
using ProcureAi.Assistant.Infrastructure.Mongo;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Assistant;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace ProcureAi.Assistant.Infrastructure;

public static class DependencyInjection
{
    /// <summary>The main store: Postgres with pgvector (documents, chunks, purchases, Wolverine's outbox and inbox).</summary>
    public const string DatabaseConnectionName = "assistantdb";

    /// <summary>The second store: MongoDB for conversations and invoice scans (spec 5).</summary>
    public const string ChatDatabaseConnectionName = "assistantchatdb";

    /// <summary>The local durable queue of the ingestion jobs (<see cref="IngestPolicyDocument"/>).</summary>
    public const string IngestionQueue = "assistant-ingestion";

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
        services.AddAssistantIngestion();
        return services;
    }

    /// <summary>
    /// Wolverine on the Postgres message store of <c>assistantdb</c> (durable inbox and outbox, EF Core transactions, the shared
    /// <c>assistant-api-access-changed</c> queue), and the Assistant's own routes: ingestion jobs on the local durable queue
    /// <c>assistant-ingestion</c> (at most <c>Assistant:Ingestion:MaxParallelDocuments</c> at a time; a job survives a restart), and the
    /// policy document events on the <c>assistant</c> exchange. A model provider failure of a job
    /// (<see cref="AiProviderUnavailableException"/>) is retried on the durable schedule of <c>Assistant:Ingestion:RetryDelays</c>; the
    /// job itself marks the version <c>Failed</c> on its last attempt, so the rule's final step (the error queue) is only a safety net.
    /// Past purchases come with plan 05 Task 3.
    /// </summary>
    public static void ConfigureAssistant(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiMessaging<IAssistantDbContext, AssistantDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(AssistantOptions).Assembly);

        var ingestion = configuration.GetSection(IngestionOptions.SectionPath).Get<IngestionOptions>() ?? new IngestionOptions();
        options.LocalQueue(IngestionQueue).UseDurableInbox().MaximumParallelMessages(ingestion.MaxParallelDocuments);
        options.PublishMessage<IngestPolicyDocument>().ToLocalQueue(IngestionQueue);
        options.PublishMessage<PolicyDocumentProgressed>().ToRabbitExchange(Exchanges.Assistant);
        options.PublishMessage<PolicyDocumentStatusChanged>().ToRabbitExchange(Exchanges.Assistant);
        // The job commits step by step through its own units of work: resolved from the container, so Wolverine neither inlines it nor
        // wraps it in one transaction.
        options.CodeGeneration.AlwaysUseServiceLocationFor<PolicyIngestion>();

        var delays = ingestion.RetryDelaySchedule();
        if (delays.Length == 0)
        {
            throw new InvalidOperationException($"'{IngestionOptions.SectionPath}:{nameof(IngestionOptions.RetryDelays)}' is not valid.");
        }

        options.OnException<AiProviderUnavailableException>().ScheduleRetry(delays).Then.MoveToErrorQueue();
    }
}
