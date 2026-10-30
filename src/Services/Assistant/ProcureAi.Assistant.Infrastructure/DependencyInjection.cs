using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;
using ProcureAi.Assistant.Infrastructure.Ai;
using ProcureAi.Assistant.Infrastructure.Chat;
using ProcureAi.Assistant.Infrastructure.Ingestion;
using ProcureAi.Assistant.Infrastructure.Mongo;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.Assistant.Infrastructure.Retrieval;
using ProcureAi.Assistant.Infrastructure.Scanning;
using ProcureAi.Assistant.Infrastructure.Tools;
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

    /// <summary>The queue of the past purchase events, bound to the <c>requests</c> and <c>approvals</c> exchanges.</summary>
    public const string RequestsQueue = "assistant-api-requests";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, pgvector enabled, audited), the chat database on MongoDB and the model providers
    /// and pipelines (<see cref="AiSetup.AddAssistantAi"/>), and the tools of the model (<see cref="ToolsSetup.AddAssistantTools"/>). The caller
    /// registers <see cref="BuildingBlocks.Application.ICallerContext"/>.
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
        services.AddAssistantRetrieval();
        services.AddAssistantChat();
        services.AddAssistantTools(configuration);
        services.AddAssistantScanning();
        services.AddHostedService<PlaceholderCleanup>();
        return services;
    }

    /// <summary>
    /// Wolverine on the Postgres message store of <c>assistantdb</c> (durable inbox and outbox, EF Core transactions, the shared
    /// <c>assistant-api-access-changed</c> queue), and the Assistant's own routes: ingestion jobs on the local durable queue
    /// <c>assistant-ingestion</c> (at most <c>Assistant:Ingestion:MaxParallelDocuments</c> at a time; a job survives a restart), and the
    /// policy document events on the <c>assistant</c> exchange. A model provider failure of a job
    /// (<see cref="AiProviderUnavailableException"/>) is retried on the durable schedule of <c>Assistant:Ingestion:RetryDelays</c>; the
    /// job itself marks the version <c>Failed</c> on its last attempt, so the rule's final step (the error queue) is only a safety net.
    /// <para>
    /// Past purchases (ruling 9): the durable queue <c>assistant-api-requests</c>, bound to the <c>requests</c> and <c>approvals</c>
    /// exchanges. Only <c>RequestSubmitted</c>, <c>RequestApproved</c> and <c>RequestCompleted</c> have handlers; the other events of the two
    /// exchanges are discarded by Wolverine ("no known handler", logged, never dead-lettered). A completion that arrived before its submit
    /// data (<see cref="PurchaseDataMissingException"/>) is retried on the durable schedule of <c>Assistant:Purchases:RetryDelays</c>; the
    /// handler itself ends the message on the attempt after the last retry, so the error queue is only a safety net. Two events of one
    /// request that race on its row are retried inline (<c>Assistant:Purchases:ConcurrencyRetry</c>), a failed Access lookup of the
    /// approvers' names on a schedule (<c>Assistant:Purchases:DependencyRetry</c>). These rules are set on the three handler chains only
    /// (<see cref="PastPurchaseFailurePolicy"/>), so the policy document commands and the ingestion job are unaffected.
    /// </para>
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

        ConfigurePastPurchases(options, configuration, delays);
    }

    private static void ConfigurePastPurchases(WolverineOptions options, IConfiguration configuration, TimeSpan[] providerDelays)
    {
        // The requests and approvals exchanges are fanout exchanges declared by their publishers (Wolverine's default), so a binding
        // cannot pick event types by routing key without changing every publisher: the queue gets every event of both. Only the three of
        // PastPurchaseFailurePolicy.Messages have handlers here; Wolverine discards the others ("had no known handlers and was discarded",
        // logged at Information level, never dead-lettered; see Rejected_request_is_not_indexed).
        options.ListenToRabbitQueue(RequestsQueue, queue =>
        {
            queue.BindExchange(Exchanges.Requests);
            queue.BindExchange(Exchanges.Approvals);
        });
        // Resolved from the container (it needs the Access client, a typed HttpClient), with the handler's DbContext passed in.
        options.CodeGeneration.AlwaysUseServiceLocationFor<PurchaseIndexer>();

        var purchases = configuration.GetSection(PastPurchasesOptions.SectionPath).Get<PastPurchasesOptions>() ?? new PastPurchasesOptions();
        if (purchases.RetryDelaySchedule().Length == 0)
        {
            throw new InvalidOperationException(
                $"'{PastPurchasesOptions.SectionPath}:{nameof(PastPurchasesOptions.RetryDelays)}' is not valid.");
        }

        // Validates the retry sections now (startup), not when the handler chains are built.
        purchases.ConcurrencyRetry.Delays($"{PastPurchasesOptions.SectionPath}:{nameof(PastPurchasesOptions.ConcurrencyRetry)}");
        purchases.DependencyRetry.Delays($"{PastPurchasesOptions.SectionPath}:{nameof(PastPurchasesOptions.DependencyRetry)}");
        options.Policies.Add(new PastPurchaseFailurePolicy(purchases, providerDelays));
    }
}
