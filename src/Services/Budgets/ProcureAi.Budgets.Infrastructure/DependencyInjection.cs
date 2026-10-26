using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ProcureAi.Budgets.Application;
using ProcureAi.Budgets.Application.Abstractions;
using ProcureAi.Budgets.Application.Commands;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Budgets;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace ProcureAi.Budgets.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "budgetsdb";

    /// <summary>The Postgres error for a unique key violated by a concurrent insert.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, so its changes and outgoing messages commit together).
    /// The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/> and the <c>Budgets</c> options.
    /// </summary>
    public static IServiceCollection AddBudgetsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        // Options (and so the interceptor) are shared app-wide; the interceptor reads the caller from the saving DbContext.
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<BudgetsDbContext>(
            (sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IBudgetsDbContext>(sp => sp.GetRequiredService<BudgetsDbContext>());
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// Wolverine setup (shared messaging building block), the queue <c>budgets-commands</c> (ReserveBudget, ReleaseBudget), the
    /// outcome events to the <c>budgets</c> exchange (one route per event type, so each is routed by its own type), and the
    /// retries for a handler that lost a race in the database: a concurrent change of the same budget row
    /// (<see cref="DbUpdateConcurrencyException"/>) or a concurrent insert of the same request marker (unique violation).
    /// Backoff and attempts (inline retries with per-failure jitter) come from <c>Budgets:ConcurrencyRetry</c>.
    /// </summary>
    public static void ConfigureBudgets(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiMessaging<IBudgetsDbContext, BudgetsDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(ReserveBudgetHandler).Assembly);
        options.ListenToRabbitQueue(Queues.BudgetCommands);
        options.PublishMessage<BudgetReserved>().ToRabbitExchange(Exchanges.Budgets);
        options.PublishMessage<BudgetReservationFailed>().ToRabbitExchange(Exchanges.Budgets);
        options.PublishMessage<BudgetReleased>().ToRabbitExchange(Exchanges.Budgets);

        var retry = configuration.GetSection(BudgetsOptions.SectionName).Get<BudgetsOptions>()?.ConcurrencyRetry
            ?? throw new InvalidOperationException($"'{BudgetsOptions.SectionName}:{nameof(BudgetsOptions.ConcurrencyRetry)}' is not configured.");
        // Inline retries with a fresh random jitter added at every failure; after the last attempt the message goes to the
        // dead-letter queue. (Scheduled retries were tried and rejected: they come back in lumps at the scheduled-job polling
        // interval, so a burst on one budget re-collided each time and 20 contenders still dead-lettered with 10 attempts.
        // Waiting inline also caps the contenders at the listener parallelism of the instances.)
        var delays = retry.Delays();
        InlineRetry(options.OnException<DbUpdateConcurrencyException>(), delays, retry.JitterFraction);
        InlineRetry(
            options.OnException<DbUpdateException>(exception => exception.InnerException is PostgresException { SqlState: UniqueViolation }),
            delays,
            retry.JitterFraction);
    }

    private static void InlineRetry(PolicyExpression when, TimeSpan[] delays, double jitterFraction)
    {
        var retries = when.RetryWithCooldown(delays);
        // Wolverine rejects a jitter of zero; zero means none.
        (jitterFraction > 0 ? retries.WithBoundedJitter(jitterFraction) : retries).Then.MoveToErrorQueue();
    }
}
