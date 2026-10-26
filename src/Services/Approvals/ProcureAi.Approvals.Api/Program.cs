using ProcureAi.Approvals.Api;
using ProcureAi.Approvals.Infrastructure;
using ProcureAi.BuildingBlocks.Hosting;

var app = ApprovalsApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the database and
// Wolverine message store are migrated, and the process exits. Approvals has no demo seed: its data comes from the flow.
// Nothing migrates at normal startup.
await ServiceHost.RunAsync(
    app,
    args,
    (services, cancellationToken) => services.MigrateApprovalsDatabaseAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
