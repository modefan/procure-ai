using ProcureAi.Assistant.Api;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.BuildingBlocks.Hosting;

var app = AssistantApi.Build(args);

// With --migrate (run by the deployment before the API starts: Aspire resource / Kubernetes job) the chat database's indexes, the
// Postgres schema (pgvector extension, documents, chunks, purchases) and Wolverine's message store are migrated and the process
// exits. Nothing migrates at normal startup. The demo seed (the procurement policy) comes with plan 05 Task 2b.
await ServiceHost.RunAsync(app, args, (services, cancellationToken) => services.MigrateAssistantDatabaseAsync(cancellationToken));

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
