using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts.Requests;
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
    public const string RequestsExchange = "requests";

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
        services.AddScoped<IRequestNumberGenerator, RequestNumberGenerator>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// Wolverine setup (shared messaging building block): handler discovery, Postgres outbox/inbox in the requests
    /// database, EF Core transactions around handlers, RabbitMQ, and <c>RequestSubmitted</c> going to the
    /// <c>requests</c> exchange.
    /// </summary>
    public static void ConfigureRequests(this WolverineOptions options, IConfiguration configuration)
    {
        options.UseProcureAiMessaging<IRequestsDbContext, RequestsDbContext>(
            configuration, DatabaseConnectionName, typeof(CreateRequest).Assembly);
        options.PublishMessage<RequestSubmitted>().ToRabbitExchange(RequestsExchange);
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"Connection string '{name}' is not configured.");
}
