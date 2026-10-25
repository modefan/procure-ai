using ProcureAi.BuildingBlocks.Messaging.Hosting;
using ProcureAi.Requests.Api;
using ProcureAi.Requests.Infrastructure;

var app = RequestsApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the database and
// Wolverine's message store are migrated and the process exits. Nothing migrates at normal startup.
await ServiceHost.RunAsync(app, args, (services, cancellationToken) => services.MigrateRequestsDatabaseAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
