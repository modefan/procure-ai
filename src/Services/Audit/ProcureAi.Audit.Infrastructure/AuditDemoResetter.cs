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
    private static readonly string[] Queues =
        [DependencyInjection.CdcQueue, DependencyInjection.CdcMongoQueue, DependencyInjection.EventsQueue];

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await WaitUntilQuietAsync(cancellationToken);
        await WolverineDemoReset.ClearEnvelopesAsync(
            configuration.GetConnectionString(DependencyInjection.DatabaseConnectionName)
            ?? throw new InvalidOperationException("The audit database connection string is missing."),
            cancellationToken);
        await store.Advanced.Clean.DeleteAllDocumentsAsync(cancellationToken);
        var deadLetters = await WolverineDemoReset.PurgeBrokerDeadLettersAsync(configuration, cancellationToken);
        logger.LogInformation("Demo reset deleted every audit document and {DeadLetters} broker dead letters", deadLetters);
    }

    /// <summary>
    /// Waits until the change data capture and events of the other services' resets are all handled: Audit's queues on the broker and its
    /// inbox stay empty for <see cref="DemoResetOptions.QuietPeriod"/>. One reset transaction can carry hundreds of changes; clearing while
    /// they are handled would keep the later half without the earlier (each change's interceptor row would be missing: false alerts).
    /// </summary>
    private async Task WaitUntilQuietAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection(DemoResetOptions.SectionName).Get<DemoResetOptions>() ?? new DemoResetOptions();
        var poll = TimeSpan.FromMilliseconds(500);
        var deadline = DateTimeOffset.UtcNow + options.DrainTimeout;
        var quietSince = (DateTimeOffset?)null;
        while (true)
        {
            var waiting = await WolverineDemoReset.QueueDepthAsync(configuration, Queues, cancellationToken)
                          + (await runtime.Storage.Admin.FetchCountsAsync()).Incoming;
            var now = DateTimeOffset.UtcNow;
            if (waiting > 0)
            {
                quietSince = null;
            }
            else if ((quietSince ??= now) + options.QuietPeriod <= now)
            {
                return;
            }

            if (now > deadline)
            {
                throw new TimeoutException(
                    $"Audit still had {waiting} message(s) to handle after {options.DrainTimeout}; the reset was not run (try again).");
            }

            await Task.Delay(poll, cancellationToken);
        }
    }
}
