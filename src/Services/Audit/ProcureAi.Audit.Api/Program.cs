using ProcureAi.Audit.Api;
using ProcureAi.Audit.Infrastructure;
using ProcureAi.BuildingBlocks.Hosting;

var app = AuditApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) Marten's schema and the
// Wolverine message store are created or updated, and the process exits. Nothing migrates at normal startup. No demo seed:
// the audit is filled by the other services' events.
await ServiceHost.RunAsync(
    app,
    args,
    (services, cancellationToken) => services.MigrateAuditDatabaseAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
