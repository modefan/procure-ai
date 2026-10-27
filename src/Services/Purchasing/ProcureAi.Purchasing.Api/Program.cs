using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.Purchasing.Api;
using ProcureAi.Purchasing.Infrastructure;

var app = PurchasingApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the database and
// Wolverine's message store are migrated and the process exits. Nothing migrates at normal startup. There is no demo seed:
// orders come from approved requests.
await ServiceHost.RunAsync(app, args, (services, cancellationToken) => services.MigratePurchasingDatabaseAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
