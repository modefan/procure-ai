using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Demo;
using Wolverine.Runtime;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>
/// The Audit part of the nightly demo reset (plan 06 rulings 4 and 5), called last, after the other services and a grace period for late
/// change data capture: Wolverine's envelopes (also the pending attribution timeouts), every Marten document (business events, data
/// changes, matches, interceptor rows, outside-the-app alerts) and the broker's shared dead-letter queue. The pgaudit log is the server's
/// and is not touched. Nothing is seeded: the audit fills again from the events of the new demo data.
/// </summary>
public sealed class AuditDemoResetter(
    IDocumentStore store, IWolverineRuntime runtime, IConfiguration configuration, ILogger<AuditDemoResetter> logger) : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await WolverineDemoReset.ClearMessageStoreAsync(runtime);
        await store.Advanced.Clean.DeleteAllDocumentsAsync(cancellationToken);
        var deadLetters = await WolverineDemoReset.PurgeBrokerDeadLettersAsync(configuration, cancellationToken);
        logger.LogInformation("Demo reset deleted every audit document and {DeadLetters} broker dead letters", deadLetters);
    }
}
