using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;
using Wolverine;

namespace ProcureAi.Audit.Application.Cdc;

/// <summary>
/// Takes every change Debezium publishes for the captured MongoDB collections (queue <c>audit-cdc-mongo</c>, plan 06 ruling 16). There is
/// no transaction to match, so a change is decided as it arrives, from the <c>_audit</c> stamp it carries and the document's previous
/// change (<see cref="MongoChange.ToDataChange"/>); one made outside the app raises an alert at once. Values are never logged.
/// </summary>
public static class MongoChangeHandler
{
    public static async Task Handle(
        DebeziumMongoMessage message, Envelope envelope, IDataChangeStore store, IAuditMetrics metrics, IOptions<AuditOptions> options,
        TimeProvider clock, ILogger<MongoChange> logger, CancellationToken cancellationToken)
    {
        envelope.Headers.TryGetValue(DebeziumMongoMessage.DocumentKeyHeader, out var documentKey);
        var change = MongoChangeParser.Parse(message, documentKey);
        if (change is null)
        {
            logger.LogDebug("Ignored a MongoDB CDC message that is not a change of one document ({Operation})", message.Operation);
            return;
        }

        var previous = await store.LatestChangeAsync(change.Database, change.Collection, change.DocumentKey, change.Id, cancellationToken);
        var dataChange = change.ToDataChange(previous, options.Value.Cdc);
        if (!await store.RecordDecidedChangeAsync(dataChange, clock.GetUtcNow(), cancellationToken))
        {
            return; // redelivery
        }

        if (dataChange.Attribution.Kind == AttributionKind.OutsideApp)
        {
            metrics.OutsideAppChange(dataChange.Database, dataChange.Table);
            logger.LogWarning(
                "Changed outside the app: {Operation} of {Database}.{Collection} at {CommittedAt} (change {ChangeId})",
                dataChange.Operation, dataChange.Database, dataChange.Table, dataChange.CommittedAt, dataChange.Id);
        }
    }
}
