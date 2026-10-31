using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Demo;
using Wolverine.Runtime;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>
/// The Requests part of the nightly demo reset (plan 06 ruling 4): Wolverine's envelopes, then every row of every tenant (tracked deletes, so
/// the audit interceptor attributes each to the app) and the number counters. The service has no seed of its own: ProcureAi.DemoData
/// creates its data again through the API.
/// </summary>
public sealed class RequestsDemoResetter(RequestsDbContext db, IWolverineRuntime runtime, ILogger<RequestsDemoResetter> logger) : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await WolverineDemoReset.ClearMessageStoreAsync(runtime);
        var deleted = await EfDemoReset.DeleteAllRowsAsync(db, cancellationToken: cancellationToken);
        logger.LogInformation("Demo reset deleted {Count} rows", deleted);
    }
}
