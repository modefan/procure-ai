using ProcureAi.Budgets.Api;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Hosting;

var app = BudgetsApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the database and
// Wolverine message store are migrated, the demo data is seeded when DemoSeed:Enabled is true, and the process exits.
// Nothing migrates at normal startup.
await ServiceHost.RunAsync(
    app,
    args,
    (services, cancellationToken) => services.MigrateBudgetsDatabaseAsync(cancellationToken),
    (services, cancellationToken) => services.SeedDemoDataAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
