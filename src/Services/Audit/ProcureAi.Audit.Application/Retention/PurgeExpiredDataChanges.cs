using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;

namespace ProcureAi.Audit.Application.Retention;

/// <summary>Sent every <c>Audit:PurgeInterval</c> (daily): deletes the data change audit older than <c>Audit:RetentionDays</c>.</summary>
public sealed record PurgeExpiredDataChanges;

public static class PurgeExpiredDataChangesHandler
{
    public static async Task Handle(
        PurgeExpiredDataChanges message, IDataChangeStore store, IOptions<AuditOptions> options, TimeProvider clock,
        ILogger<PurgeExpiredDataChanges> logger, CancellationToken cancellationToken)
    {
        var audit = options.Value;
        var cutoff = clock.GetUtcNow().AddDays(-audit.RetentionDays);
        var purged = await store.PurgeAsync(cutoff, audit.PurgeBatchSize, cancellationToken);
        logger.LogInformation("Purged {Count} data change audit documents from before {Cutoff}", purged, cutoff);
    }
}
