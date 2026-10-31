using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Requests;
using ProcureAi.Requests.Application;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Commands;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace ProcureAi.Requests.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "requestsdb";
    public const string RabbitMqConnectionName = MessagingSetup.RabbitMqConnectionName;

    /// <summary>The exchange integration events of this service are published to.</summary>
    public const string RequestsExchange = Exchanges.Requests;

    /// <summary>The Requests queue bound to the <c>approvals</c> exchange (the saga's outcome: RequestApproved, RequestRejected).</summary>
    public const string ApprovalsQueue = "requests-approvals";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, so its changes and outgoing messages commit together) and the
    /// number generator. The caller registers <see cref="ICallerContext"/> and the <c>Paging</c> options.
    /// </summary>
    public static IServiceCollection AddRequestsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = Required(configuration, DatabaseConnectionName);
        // Options (and so the interceptor) are shared app-wide; the interceptor reads the caller from the saving DbContext.
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<RequestsDbContext>(
            (sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IRequestsDbContext>(sp => sp.GetRequiredService<RequestsDbContext>());
        services.AddTenantSequence<RequestsDbContext>();
        services.AddScoped<IRequestNumberGenerator, RequestNumberGenerator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ProcureAi.BuildingBlocks.Web.Demo.IDemoResetter, RequestsDemoResetter>();
        return services;
    }

    /// <summary>
    /// The approval pre-check before a submission (<see cref="IApprovalPreview"/>): a client of the Approvals service, with
    /// retries and timeouts from the <c>ApprovalsClient</c> section. Registered by the API (it needs <c>Auth:Internal</c>, the
    /// service's own login).
    /// </summary>
    public static IServiceCollection AddApprovalPreview(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ApprovalsClientOptions>(configuration, ApprovalsClientOptions.SectionName);
        services.AddProcureAiServiceClient<IApprovalPreview, ApprovalPreviewClient>(ApprovalPreviewClient.ApprovalsServiceName)
            .WithResilienceFrom<ApprovalsClientOptions>();
        return services;
    }

    /// <summary>
    /// The supplier catalog (<see cref="ISupplierCatalog"/>): a client of the Suppliers service, with retries and timeouts and the
    /// cache durations from the <c>SuppliersClient</c> section. Registered by the API (it needs <c>Auth:Internal</c>).
    /// </summary>
    public static IServiceCollection AddSupplierCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<SuppliersClientOptions>(configuration, SuppliersClientOptions.SectionName);
        services.AddProcureAiServiceClient<ISupplierCatalog, SuppliersClient>(SuppliersClient.SuppliersServiceName)
            .WithResilienceFrom<SuppliersClientOptions>();
        return services;
    }

    /// <summary>
    /// Wolverine setup (shared messaging building block): handler discovery, Postgres outbox/inbox in the requests
    /// database, EF Core transactions around handlers, RabbitMQ, <c>RequestSubmitted</c> and <c>RequestCancelled</c> going to
    /// the <c>requests</c> exchange (where the approval saga listens), and the saga's outcome arriving on
    /// <c>requests-approvals</c> (bound to the <c>approvals</c> exchange). An outcome handler that lost a race with a cancel of
    /// the same request is retried (<c>Requests:ConcurrencyRetry</c>).
    /// </summary>
    public static void ConfigureRequests(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiMessaging<IRequestsDbContext, RequestsDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(CreateRequest).Assembly);
        options.PublishMessage<RequestSubmitted>().ToRabbitExchange(RequestsExchange);
        options.PublishMessage<RequestCancelled>().ToRabbitExchange(RequestsExchange);
        options.ListenToRabbitQueue(ApprovalsQueue, queue => queue.BindExchange(Exchanges.Approvals));
        // A typed HttpClient (a factory registration Wolverine cannot inline): the submit handler resolves it from the container.
        options.CodeGeneration.AlwaysUseServiceLocationFor<IApprovalPreview>();
        options.CodeGeneration.AlwaysUseServiceLocationFor<ISupplierCatalog>();

        var section = $"{RequestsOptions.SectionName}:{nameof(RequestsOptions.ConcurrencyRetry)}";
        var retry = configuration.GetSection(RequestsOptions.SectionName).Get<RequestsOptions>()?.ConcurrencyRetry
            ?? throw new InvalidOperationException($"'{section}' is not configured.");
        options.RetryConcurrencyConflicts(retry, section);
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"Connection string '{name}' is not configured.");
}
