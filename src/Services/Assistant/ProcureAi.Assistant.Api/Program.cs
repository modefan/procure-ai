using ProcureAi.Assistant.Api;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.BuildingBlocks.Hosting;

var app = AssistantApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the chat database's indexes, the
// Postgres schema (pgvector extension, documents, chunks, purchases) and Wolverine's message store are migrated and the process
// exits. Nothing migrates at normal startup. With DemoSeed:Enabled the procurement policy is seeded for the demo tenants and its ingestion
// queued (the running api embeds it; the migrate run never calls the model).
await ServiceHost.RunAsync(
    app,
    args,
    (services, cancellationToken) => services.MigrateAssistantDatabaseAsync(cancellationToken),
    (services, cancellationToken) => services.SeedDemoDataAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
