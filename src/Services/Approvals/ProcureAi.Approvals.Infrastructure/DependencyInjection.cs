using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Approvals.Application;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Budgets;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace ProcureAi.Approvals.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "approvalsdb";

    /// <summary>The Approvals queue bound to the <c>requests</c> exchange (RequestSubmitted, RequestCancelled).</summary>
    public const string RequestsQueue = "approvals-requests";

    /// <summary>The Approvals queue bound to the <c>budgets</c> exchange (the outcome of a reservation).</summary>
    public const string BudgetsQueue = "approvals-budgets";

    /// <summary>
    /// Registers the DbContext (Wolverine-integrated, so the saga, the steps and outgoing messages commit together).
    /// The caller registers <see cref="BuildingBlocks.Application.ICallerContext"/> and the <c>Approvals</c> options.
    /// </summary>
    public static IServiceCollection AddApprovalsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        // Options (and so the interceptor) are shared app-wide; the interceptor reads the caller from the saving DbContext.
        services.TryAddSingleton<AuditSaveChangesInterceptor>();
        services.AddDbContextWithWolverineIntegration<ApprovalsDbContext>(
            (sp, options) => options.UseNpgsql(connectionString).AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>()));
        // Forwards to the same scoped DbContext instance (a factory, not AddScoped<,>, which would create a second one).
        services.AddScoped<IApprovalsDbContext>(sp => sp.GetRequiredService<ApprovalsDbContext>());
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    /// Wolverine setup (shared messaging building block) for the approval saga: it listens to the <c>requests</c> and
    /// <c>budgets</c> exchanges on queues of its own, sends <c>ReserveBudget</c> / <c>ReleaseBudget</c> to <c>budgets-commands</c>,
    /// publishes its events to the <c>approvals</c> exchange, and schedules step timeouts on durable local queues (stored in
    /// the database, so a timeout survives a restart). A saga message that lost a race with another message of the same
    /// request is retried (<c>Approvals:ConcurrencyRetry</c>), and one whose call to Access failed is retried later
    /// (<c>Approvals:DependencyRetry</c>), so an Access outage delays the chain instead of losing the message.
    /// </summary>
    public static void ConfigureApprovals(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.UseProcureAiMessaging<IApprovalsDbContext, ApprovalsDbContext>(
            configuration, serviceName, DatabaseConnectionName, typeof(ApprovalSaga).Assembly);
        options.Policies.UseDurableLocalQueues();

        options.ListenToRabbitQueue(RequestsQueue, queue => queue.BindExchange(Exchanges.Requests));
        options.ListenToRabbitQueue(BudgetsQueue, queue => queue.BindExchange(Exchanges.Budgets));

        options.PublishMessage<ReserveBudget>().ToRabbitQueue(Queues.BudgetCommands);
        options.PublishMessage<ReleaseBudget>().ToRabbitQueue(Queues.BudgetCommands);
        options.PublishMessage<ApprovalRequested>().ToRabbitExchange(Exchanges.Approvals);
        options.PublishMessage<ApprovalStepApproved>().ToRabbitExchange(Exchanges.Approvals);
        options.PublishMessage<ApprovalReminderDue>().ToRabbitExchange(Exchanges.Approvals);
        options.PublishMessage<ApprovalEscalated>().ToRabbitExchange(Exchanges.Approvals);
        options.PublishMessage<ApprovalAlertRaised>().ToRabbitExchange(Exchanges.Approvals);
        options.PublishMessage<RequestApproved>().ToRabbitExchange(Exchanges.Approvals);
        options.PublishMessage<RequestRejected>().ToRabbitExchange(Exchanges.Approvals);

        var approvals = configuration.GetSection(ApprovalsOptions.SectionName).Get<ApprovalsOptions>()
            ?? throw new InvalidOperationException($"'{ApprovalsOptions.SectionName}' is not configured.");
        options.RetryConcurrencyConflicts(
            approvals.ConcurrencyRetry, $"{ApprovalsOptions.SectionName}:{nameof(ApprovalsOptions.ConcurrencyRetry)}");
        options.RetryTransientDependencyFailures(
            approvals.DependencyRetry, $"{ApprovalsOptions.SectionName}:{nameof(ApprovalsOptions.DependencyRetry)}");
    }
}
