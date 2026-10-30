using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Invoicing;
using ProcureAi.Invoicing.Application;
using ProcureAi.Invoicing.Application.Abstractions;
using ProcureAi.Invoicing.Application.Commands;
using ProcureAi.Invoicing.Infrastructure.Assistant;
using ProcureAi.Invoicing.Infrastructure.Erp;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace ProcureAi.Invoicing.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "invoicingdb";

    /// <summary>Invoicing's queue of Purchasing's events (bound to the <c>purchasing</c> exchange).</summary>
    public const string PurchasingQueue = "invoicing-purchasing";

    /// <summary>The local durable queue of Invoicing's own work: ERP exports and adopting invoices that waited for their order.</summary>
    public const string LocalQueue = "invoicing-work";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, so its changes and outgoing messages commit together), the arrival and payment
    /// numbers, the webhook inbox and the metrics. The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/> and the
    /// <c>Invoicing</c> options.
    /// </summary>
    public static IServiceCollection AddInvoicingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        // Options (and so the interceptor) are shared app-wide; the interceptor reads the caller from the saving DbContext.
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<InvoicingDbContext>(
            (sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IInvoicingDbContext>(sp => sp.GetRequiredService<InvoicingDbContext>());
        services.AddTenantSequence<InvoicingDbContext>();
        services.AddWebhookInbox<InvoicingDbContext>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IInvoicingMetrics, InvoicingMetrics>();
        return services;
    }

    /// <summary>
    /// The ERP anti-corruption layer (<see cref="IErpGateway"/>): a typed client of the ERP's payment API with the key in
    /// <c>X-Api-Key</c> and its own resilience pipeline <c>erp</c>, all from the <c>ErpClient</c> section (<see cref="ExternalSystemClient"/>).
    /// </summary>
    public static IServiceCollection AddErpGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddExternalSystemClient<IErpGateway, ErpSimulatorGateway, ErpClientOptions>(
            configuration, ErpClientOptions.SectionName, ErpSimulatorGateway.ClientName, ErpSimulatorGateway.BreakerName);
        return services;
    }

    /// <summary>
    /// The Assistant's stored invoice scans (<see cref="IAssistantScans"/>, plan 05 Task 6): a client of the Assistant service as Invoicing
    /// (<c>Auth:Internal</c>), with retries and timeouts from the <c>AssistantClient</c> section.
    /// </summary>
    public static IServiceCollection AddAssistantScans(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<AssistantClientOptions>(configuration, AssistantClientOptions.SectionName);
        services.AddProcureAiServiceClient<IAssistantScans, AssistantScansClient>(AssistantScansClient.AssistantServiceName)
            .WithResilienceFrom<AssistantClientOptions>();
        return services;
    }

    /// <summary>
    /// Wolverine setup (shared messaging building block) and the routes of Invoicing: Purchasing's events arrive on
    /// <c>invoicing-purchasing</c>; ERP exports and the adoption of waiting invoices go through the local durable queue
    /// <c>invoicing-work</c> (a retry scheduled with backoff survives a restart); every event goes to the <c>invoicing</c> exchange.
    /// <para>
    /// Error rules (Wolverine 6.53 dead-letters a failure no rule matches on the first try): a handler that lost a race in the
    /// database (a concurrent change of an invoice or order view, <see cref="DbUpdateConcurrencyException"/>, or a concurrent insert of
    /// the same key, such as one supplier invoice number or one webhook event) is retried inline from <c>Invoicing:ConcurrencyRetry</c>
    /// and then sees the winner's outcome. A message about an order Invoicing has no copy of yet (<see cref="OrderViewMissingException"/>:
    /// a delivery, close short or export before <c>PurchaseOrderCreated</c>, or waiting invoices to adopt) is retried on the durable
    /// schedule of <c>Invoicing:OrderViewRetry</c>. A call to another service that fails for a passing reason is retried on the schedule
    /// of <c>Invoicing:DependencyRetry</c>.
    /// </para>
    /// </summary>
    public static void ConfigureInvoicing(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiMessaging<IInvoicingDbContext, InvoicingDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(RecordInvoiceHandler).Assembly);
        options.ListenToRabbitQueue(PurchasingQueue, queue => queue.BindExchange(Exchanges.Purchasing));
        options.LocalQueue(LocalQueue).UseDurableInbox();
        options.PublishMessage<ExportPayment>().ToLocalQueue(LocalQueue);
        options.PublishMessage<AttachWaitingInvoices>().ToLocalQueue(LocalQueue);
        options.PublishMessage<InvoiceReceived>().ToRabbitExchange(Exchanges.Invoicing);
        options.PublishMessage<InvoiceMatched>().ToRabbitExchange(Exchanges.Invoicing);
        options.PublishMessage<InvoiceMismatched>().ToRabbitExchange(Exchanges.Invoicing);
        options.PublishMessage<PaymentReleased>().ToRabbitExchange(Exchanges.Invoicing);
        options.PublishMessage<PaymentExported>().ToRabbitExchange(Exchanges.Invoicing);
        options.PublishMessage<PurchaseOrderSettled>().ToRabbitExchange(Exchanges.Invoicing);
        // A typed HttpClient (a factory registration Wolverine cannot inline): the export handler resolves it from the container.
        options.CodeGeneration.AlwaysUseServiceLocationFor<IErpGateway>();
        options.CodeGeneration.AlwaysUseServiceLocationFor<IAssistantScans>();

        var invoicing = configuration.GetSection(InvoicingOptions.SectionName).Get<InvoicingOptions>()
            ?? throw new InvalidOperationException($"'{InvoicingOptions.SectionName}' is not configured.");
        options.RetryConcurrencyConflicts(
            invoicing.ConcurrencyRetry, $"{InvoicingOptions.SectionName}:{nameof(InvoicingOptions.ConcurrencyRetry)}");
        options.OnException<OrderViewMissingException>()
            .ScheduleRetry(invoicing.OrderViewRetry.Delays($"{InvoicingOptions.SectionName}:{nameof(InvoicingOptions.OrderViewRetry)}"))
            .Then.MoveToErrorQueue();
        options.RetryTransientDependencyFailures(
            invoicing.DependencyRetry, $"{InvoicingOptions.SectionName}:{nameof(InvoicingOptions.DependencyRetry)}");
    }
}
