using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;
using Wolverine;

namespace ProcureAi.Audit.Application.Cdc;

/// <summary>
/// Takes every change Debezium publishes for the captured tables (queue <c>audit-cdc</c>). An <c>audit_entries</c> insert is the
/// app's interceptor row; anything else is a data change. Whichever of the two arrives second completes the match. A change
/// still unmatched is decided after <c>Audit:Matching:Grace</c> (<see cref="ResolveAttribution"/>). Values are never logged.
/// </summary>
public static class DebeziumChangeHandler
{
    public static async Task Handle(
        DebeziumChange change, IDataChangeStore store, IMessageBus bus, IOptions<AuditOptions> options, ILogger<DebeziumChange> logger,
        CancellationToken cancellationToken)
    {
        var audit = options.Value;
        if (change.IsInterceptorEntry)
        {
            await store.RecordInterceptorEntryAsync(change.ToInterceptorEntry(), change.Lsn, audit.Matching.Window, cancellationToken);
            return;
        }

        if (!change.IsRowChange)
        {
            logger.LogDebug("Ignored a CDC message without a row ({Operation} on {Database}.{Table})", change.Operation, change.Source.Database, change.Source.Table);
            return;
        }

        var dataChange = change.ToDataChange(audit.Cdc);
        var attribution = await store.RecordChangeAsync(dataChange, change.Lsn, audit.Matching.Window, cancellationToken);
        if (attribution.Kind == AttributionKind.Pending)
        {
            // Also on a redelivery of a change that is still pending: the timeout is idempotent, and this way a crash between the
            // commit and the scheduling cannot leave a change pending for ever.
            await bus.ScheduleAsync(new ResolveAttribution(dataChange.Id), audit.Matching.Grace);
        }
    }
}

/// <summary>After the grace period: a change still unmatched was made outside the app (metric, alert, warning without values).</summary>
public static class ResolveAttributionHandler
{
    public static async Task Handle(
        ResolveAttribution message, IDataChangeStore store, IAuditMetrics metrics, TimeProvider clock, ILogger<ResolveAttribution> logger,
        CancellationToken cancellationToken)
    {
        var outside = await store.ResolveAsync(message.ChangeId, clock.GetUtcNow(), cancellationToken);
        if (outside is null)
        {
            return;
        }

        metrics.OutsideAppChange(outside.Database, outside.Table);
        logger.LogWarning(
            "Changed outside the app: {Operation} of {Database}.{Table} in transaction {TransactionId} at {CommittedAt} (change {ChangeId})",
            outside.Operation, outside.Database, outside.Table, outside.TransactionId, outside.CommittedAt, outside.Id);
    }
}
