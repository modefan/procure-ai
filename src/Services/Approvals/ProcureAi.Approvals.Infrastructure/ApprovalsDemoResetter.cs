using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Demo;
using Wolverine.Runtime;

namespace ProcureAi.Approvals.Infrastructure;

/// <summary>
/// The Approvals part of the nightly demo reset (plan 06 ruling 4): Wolverine's envelopes and the service's own <c>audit_entries</c>, then every row of every tenant (tracked deletes, so
/// the audit interceptor attributes each to the app) and the number counters. The service has no seed of its own: ProcureAi.DemoData
/// creates its data again through the API.
/// </summary>
public sealed class ApprovalsDemoResetter(ApprovalsDbContext db, ILogger<ApprovalsDemoResetter> logger) : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(WolverineDemoReset.ClearEnvelopesSql, cancellationToken);
        // Yesterday's interceptor rows go first (carry-over 5); the deletes below write the reset's own.
        await EfDemoReset.ClearAuditEntriesAsync(db, cancellationToken);
        var deleted = await EfDemoReset.DeleteAllRowsAsync(db, cancellationToken: cancellationToken);
        logger.LogInformation("Demo reset deleted {Count} rows", deleted);
    }
}
