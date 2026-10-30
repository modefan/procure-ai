using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Status;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Purchasing;
using ProcureAi.Purchasing.Application;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Application.Commands;
using ProcureAi.Purchasing.Infrastructure.Supplier;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace ProcureAi.Purchasing.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "purchasingdb";

    /// <summary>The local durable queue of the supplier calls (send an order, tell the supplier about a cancel).</summary>
    public const string SupplierQueue = "purchasing-supplier";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, so its changes and outgoing messages commit together), the order numbers, the
    /// webhook inbox and the metrics. The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/> and the
    /// <c>Purchasing</c> options.
    /// </summary>
    public static IServiceCollection AddPurchasingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        // Options (and so the interceptor) are shared app-wide; the interceptor reads the caller from the saving DbContext.
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<PurchasingDbContext>(
            (sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IPurchasingDbContext>(sp => sp.GetRequiredService<PurchasingDbContext>());
        services.AddTenantSequence<PurchasingDbContext>();
        services.AddWebhookInbox<PurchasingDbContext>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PurchasingMetrics>();
        services.AddSingleton<IPurchasingMetrics>(sp => sp.GetRequiredService<PurchasingMetrics>());
        services.AddHostedService<WaitingOrdersCounter>();
        return services;
    }

    /// <summary>
    /// The supplier anti-corruption layer (<see cref="ISupplierGateway"/>): a typed client of the supplier API with the key in
    /// <c>X-Api-Key</c> and its own resilience pipeline <c>supplier</c> (total timeout, retries with backoff and jitter, circuit
    /// breaker, attempt timeout), all from the <c>SupplierClient</c> section (<see cref="ExternalSystemClient"/>).
    /// </summary>
    public static IServiceCollection AddSupplierGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddExternalSystemClient<ISupplierGateway, SupplierSimulatorGateway, SupplierClientOptions>(
            configuration, SupplierClientOptions.SectionName, SupplierSimulatorGateway.ClientName, SupplierSimulatorGateway.BreakerName);
        // The simulator's chaos switch: the same address and key, the service defaults' standard pipeline (not the supplier's breaker).
        services.AddHttpClient<ISupplierChaos, SupplierChaosClient>(SupplierChaosClient.ClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<SupplierClientOptions>>().Value;
            client.BaseAddress = options.BaseUrl;
            client.DefaultRequestHeaders.Add(ExternalSystemClient.ApiKeyHeader, options.ApiKey);
        });
        services.AddStatusContributor<SupplierChaosStatusContributor>();
        return services;
    }

    /// <summary>
    /// Wolverine setup (shared messaging building block) and the routes of Purchasing: commands from the approval saga arrive on
    /// <c>purchasing-commands</c>; the supplier calls go through the local durable queue <c>purchasing-supplier</c> (so a retry
    /// scheduled with backoff survives a restart); every event goes to the <c>purchasing</c> exchange.
    /// <para>
    /// Error rules (Wolverine 6.53 dead-letters a failure no rule matches on the first try): a handler that lost a race in the
    /// database, a concurrent change of the order (<see cref="DbUpdateConcurrencyException"/>: webhook against cancel, receipt
    /// against cancel) or a concurrent insert of the same key (unique violation: two creates or a create and a tombstone for one
    /// request, two deliveries of one webhook event), is retried inline from <c>Purchasing:ConcurrencyRetry</c> and then sees the
    /// winner's outcome. A call to another service that fails for a passing reason is retried on the schedule of
    /// <c>Purchasing:DependencyRetry</c>.
    /// </para>
    /// </summary>
    public static void ConfigurePurchasing(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiMessaging<IPurchasingDbContext, PurchasingDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(CreatePurchaseOrderHandler).Assembly);
        options.ListenToRabbitQueue(Queues.PurchasingCommands);
        options.LocalQueue(SupplierQueue).UseDurableInbox();
        options.PublishMessage<SendOrderToSupplier>().ToLocalQueue(SupplierQueue);
        options.PublishMessage<CancelOrderAtSupplier>().ToLocalQueue(SupplierQueue);
        options.PublishMessage<PurchaseOrderCreated>().ToRabbitExchange(Exchanges.Purchasing);
        options.PublishMessage<PurchaseOrderConfirmed>().ToRabbitExchange(Exchanges.Purchasing);
        options.PublishMessage<PurchaseOrderRejected>().ToRabbitExchange(Exchanges.Purchasing);
        options.PublishMessage<PurchaseOrderCancelled>().ToRabbitExchange(Exchanges.Purchasing);
        options.PublishMessage<PurchaseOrderCancellationRefused>().ToRabbitExchange(Exchanges.Purchasing);
        options.PublishMessage<GoodsReceived>().ToRabbitExchange(Exchanges.Purchasing);
        options.PublishMessage<PurchaseOrderClosedShort>().ToRabbitExchange(Exchanges.Purchasing);
        // A typed HttpClient (a factory registration Wolverine cannot inline): the supplier handlers resolve it from the container.
        options.CodeGeneration.AlwaysUseServiceLocationFor<ISupplierGateway>();

        var purchasing = configuration.GetSection(PurchasingOptions.SectionName).Get<PurchasingOptions>()
            ?? throw new InvalidOperationException($"'{PurchasingOptions.SectionName}' is not configured.");
        options.RetryConcurrencyConflicts(
            purchasing.ConcurrencyRetry, $"{PurchasingOptions.SectionName}:{nameof(PurchasingOptions.ConcurrencyRetry)}");
        options.RetryTransientDependencyFailures(
            purchasing.DependencyRetry, $"{PurchasingOptions.SectionName}:{nameof(PurchasingOptions.DependencyRetry)}");
    }
}
