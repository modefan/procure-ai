using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Budgets.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Demo;
using Wolverine.Runtime;

namespace ProcureAi.Budgets.Infrastructure;

/// <summary>
/// The Budgets part of the nightly demo reset (plan 06 ruling 4): Wolverine's envelopes, then every budget, reservation, spend and request
/// marker of every tenant is deleted and <see cref="DemoSeeder"/> writes the budgets again, in one transaction (no moment without a
/// budget). The reservations come back when ProcureAi.DemoData submits the requests again.
/// </summary>
public sealed class BudgetsDemoResetter(
    BudgetsDbContext db, IWolverineRuntime runtime, IOptions<BudgetsOptions> options, TimeProvider time, ILogger<BudgetsDemoResetter> logger)
    : IDemoResetter
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await WolverineDemoReset.ClearMessageStoreAsync(runtime);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deleted = await EfDemoReset.DeleteAllRowsAsync(db, cancellationToken: cancellationToken);
        await DemoSeeder.SeedAsync(db, FiscalYear.For(time.GetUtcNow(), options.Value.Zone), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Demo reset deleted {Count} rows and seeded the budgets again", deleted);
    }
}
