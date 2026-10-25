using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Commands;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Access;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace ProcureAi.Access.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "accessdb";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, so its changes and outgoing messages commit together).
    /// The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/>.
    /// </summary>
    public static IServiceCollection AddAccessInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        // Options (and so the interceptor) are shared app-wide; the interceptor reads the caller from the saving DbContext.
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<AccessDbContext>(
            (sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IAccessDbContext>(sp => sp.GetRequiredService<AccessDbContext>());
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Wolverine setup (shared messaging building block) and AccessChanged going to the access exchange.</summary>
    public static void ConfigureAccess(this WolverineOptions options, IConfiguration configuration)
    {
        options.UseProcureAiMessaging<IAccessDbContext, AccessDbContext>(
            configuration, DatabaseConnectionName, typeof(SetRolePermissions).Assembly);
        options.PublishMessage<AccessChanged>().ToRabbitExchange(Exchanges.Access);
    }
}
