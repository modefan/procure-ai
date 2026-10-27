using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.Suppliers.Api;
using ProcureAi.Suppliers.Infrastructure;

var app = SuppliersApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the indexes are created and the
// demo catalogs are seeded when DemoSeed:Enabled is true, and the process exits. A MongoDB service has no EF Core migration and
// no message store, so there is no migrate callback. Nothing migrates at normal startup.
await ServiceHost.RunAsync(
    app,
    args,
    migrate: null,
    (services, cancellationToken) => services.SeedDemoDataAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
