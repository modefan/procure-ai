using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.Invoicing.Api;
using ProcureAi.Invoicing.Infrastructure;

var app = InvoicingApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the database and
// Wolverine's message store are migrated and the process exits. Nothing migrates at normal startup. There is no demo seed:
// invoices come from the supplier and from finance.
await ServiceHost.RunAsync(app, args, (services, cancellationToken) => services.MigrateInvoicingDatabaseAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
