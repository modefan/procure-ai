# Service template: adding a service

Every service is built the same way from shared pieces. Nothing is copied from another service.

## Checklist

1. **Projects** (`src/Services/<Name>/`): `ProcureAi.<Name>.Domain`, `.Application`, `.Infrastructure`, `.Api`, with the
   dependency rules of CLAUDE.md. Add them to `ProcureAi.slnx`; the architecture tests pick them up by name.
2. **Api composition root** (`<Name>Api.cs`): one call to `ServiceHost.Build(args, "<name>-api", builder => ...)` from
   `ProcureAi.BuildingBlocks.Hosting`. The callback adds only what is the service's own: its options
   (`AddValidatedOptions`), its infrastructure, and `builder.Host.UseWolverine(...)` (use `UseProcureAiMessaging` /
   `UseProcureAiTransport`). `ServiceHost` already adds web, caching, the exception filter (404 / 403 / 412 / 422), the
   caller model (`ServiceIdentity`, `ICallerContext`, `IncomingMessage`), the readiness checks (Postgres, RabbitMQ, Redis) and
   the **Access client**: permissions come from the Access service through `IAccessDirectory` (cached 5 min, fail-safe 1 h,
   evicted by `AccessChanged`; Access unreachable and nothing cached means 403). Pass the service name to
   `UseProcureAiMessaging(configuration, "<name>-api", ...)`: it binds the queue `<name>-api-access-changed` to exchange `access`.
   Services call `IAccessDirectory.GetApproversAsync` / `GetEffectiveAccessAsync` for cost-center approvers and limits.
   **A service on Marten instead of EF Core** (Audit): `services.AddMarten(...).IntegrateWithWolverine(...)` in its
   Infrastructure, and `options.UseProcureAiHandlers(configuration, "<name>-api", handlers)` instead of `UseProcureAiMessaging`
   (the same handler discovery, transactions, durable inbox/outbox, transport and access-changed queue, without the EF Core
   message store). Marten's `AutoCreateSchemaObjects` also governs Wolverine's message store in it, so pass
   `ServiceHost.MigratingSetting` (`Service:Migrating`, true only in the `--migrate` run) to the infrastructure and use
   `CreateOrUpdate` then; `None` otherwise (`CreateOrUpdate` in Development).
   Handlers that record messages take `IMessageMetadata` (message ID, stable across redeliveries; correlation ID).
   Display names of users: `IAccessDirectory.GetDisplayNameAsync` (cached like the rest; null for an unknown user).
3. **`Program.cs`**: `var app = <Name>Api.Build(args); await ServiceHost.RunAsync(app, args, migrate, seedDemoData);`
   `--migrate` runs the EF Core migration, Wolverine's message-store migration and (when `DemoSeed:Enabled=true`) the demo
   seed, then exits. Also `public partial class Program;` for the integration tests.
4. **`appsettings.json`**: copy the shared sections from Requests (including `AccessClient`), and set `"Service": { "DatabaseConnectionName": "<name>db" }`.
5. **OpenAPI document**: in the Api csproj set `OpenApiDocumentName` (e.g. `<name>-v1`) and `OpenApiDatabaseConnectionName`.
   `src/Services/Directory.Build.targets` then writes `openapi/<name>-v1.json` on every build (commit it) and references the
   `Microsoft.Extensions.ApiDescription.Server` package; the project needs nothing else.
6. **AppHost**: `builder.AddProcureAiService<Projects.ProcureAi_<Name>_Api>(new ServiceSpec(...), shared)` in `AppHost.cs`
   creates the database, `<name>-migrate` and `<name>-api` with the environment, limits, wait-for-migration and `/health/ready`.
   Reference the returned API from the gateway. Add a `ServiceResourcesTests` case.
   With `CapturedTables` set, it also adds the database's own Debezium Server (see "Change data capture" below).
7. **Tests**: reference `tests/ProcureAi.Testing` for `ServiceInfraFixture` (derive from it: Postgres, RabbitMQ, Redis, the migrated database, the `cdc` exchange and Debezium on demand, plus `CdcListener` and `RabbitProbe`), `PostgresFixture`, `RabbitFixture`, `RedisFixture`, `TestLimits`,
   `TestAuth`, `ServiceApiFactory<Program>` (derive from it to add hooks), `StubAccessServer`, `StubApprovalsServer` (Approvals' internal preview), `RabbitRecorder` (records what a
   service publishes and sends) and `RabbitContainer` (a broker of its own to stop, for outage tests). Never copy a fixture.
8. **Gateway**: add the YARP route and cluster, and the service's OpenAPI document to the Swagger dropdown.

## Change data capture (Debezium)

- **One Debezium Server per captured database.** Debezium Server 3.7 runs exactly one source connector per instance
  ("each instance of Debezium Server runs exactly one connector",
  <https://debezium.io/documentation/reference/stable/operations/debezium-server.html>), so several databases cannot share
  one container. Each gets `debezium-<database>` (heap capped at 256 MB, `Resources:Debezium`), built by `AddDebeziumFor`
  from the one shared `infra/debezium/application.properties` plus environment variables: `DEBEZIUM_SOURCE_DATABASE_DBNAME`,
  `..._TOPIC_PREFIX=<database>`, `..._SLOT_NAME=debezium_<database>`, `..._TABLE_INCLUDE_LIST=public.<t>,...`.
  Routing keys on exchange `cdc` are `<database>.public.<table>`.
- **The migration creates the publication and grants**: in the service's first CDC migration call
  `migrationBuilder.EnableCdc("table", ...)` (and `DisableCdc` in `Down`). It creates `dbz_publication` if missing, adds the
  tables, sets `REPLICA IDENTITY FULL` and grants `SELECT` to `debezium`; idempotent, and it fails when the `debezium` role is
  missing. Always include `audit_entries`; never the Wolverine tables.
- **Nothing precedes the capture**: Debezium runs with `snapshot.mode=no_data` (only changes after its slot exists), so the
  `<name>-api` resource waits for `debezium-<database>` to be healthy (`/q/health`). The demo seed written by `--migrate`
  runs before Debezium starts and is deliberately **not** captured (seed data is not a user action).
- A renamed Debezium leaves its replication slot behind, and an unused logical slot stops WAL recycling. Use
  `migrationBuilder.DropInactiveReplicationSlot("<old slot>")` (Requests does this for `debezium_requests` in
  `CaptureAuditEntries`, which also adds `audit_entries` for databases created before it was captured). Dev note: an old
  `debezium-data` Docker volume from the single-Debezium days is now unused; remove it with `docker volume rm`
  (Aspire prefixes the name with the project, check `docker volume ls`).
- Aspire creates the databases; the Postgres init script only creates the `debezium` role. Debezium starts after the
  service's `-migrate` resource finished.
- Tests: `DebeziumFixture(network, postgres).StartAsync(database, tables...)` in `ProcureAi.Testing`, same mechanism.
  A test project that reads other services' changes (Audit) adds their databases with
  `ServiceInfraFixture.AddCapturedDatabaseAsync(database, migrate, tables...)` (each with its own Debezium).
- **The Audit service consumes it** (queue `audit-cdc`, bound with `#` to `cdc` only; Wolverine reads Debezium's plain JSON as
  `DebeziumChange` through `DefaultIncomingMessage`). Each change is matched to the `audit_entries` row of the same transaction,
  table and key (the app user) or flagged "changed outside the app" after `Audit:Matching:Grace`. Debezium does not say which
  columns are a table's key, so **a new captured table whose key is not `id` needs an entry in `Audit:Cdc:KeyColumns`** (Audit's
  appsettings, columns in EF key order); `CdcKeyColumnsTests` fails until it is there. A table whose key is a request ID goes into
  `Audit:Cdc:RequestTables`; a `request_id` column is found by itself. Values are sent as text (`decimal.handling.mode=string`).

## Business metrics

A service's own metrics go on a meter named `ProcureAi.<Service>` created through `IMeterFactory` (see Audit's `AuditMetrics`);
the service defaults export every `ProcureAi.*` meter over OTLP, so nothing else needs registering. Tests read them with a
`MeterListener`.

## Where the pieces live

| Piece | Location |
|---|---|
| `ServiceHost`, `ServiceOptions` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Hosting` (references Web, Messaging and Persistence; only `*.Api` projects may use it) |
| `ApplicationExceptionFilter` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Errors` |
| OpenAPI build step | `src/Services/Directory.Build.targets` |
| `AddProcureAiService`, `ServiceSpec`, `SharedResources` | `src/AppHost/ProcureAi.AppHost/ServiceResources.cs` |
| `EnableCdc` / `DisableCdc` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence/CdcMigrations.cs` |
| `AddDebeziumFor`, Debezium shared properties | `ServiceResources.cs`, `infra/debezium/application.properties` |
| Test fixtures, auth and factory | `tests/ProcureAi.Testing` (`ServiceInfraFixture` is the per-service infrastructure base) |
| Concurrency retries of message handlers | `ConcurrencyRetryOptions` (BuildingBlocks.Application, bound from the service's section) and `RetryConcurrencyConflicts` (BuildingBlocks.Messaging) |
| Retries of message handlers when a called service is down | `DependencyRetryOptions` (BuildingBlocks.Application, bound from the service's section) and `RetryTransientDependencyFailures` (BuildingBlocks.Messaging): scheduled retries, then dead letters |
| A client of another service of ours: `AddProcureAiServiceClient<TClient, TImpl>("<name>-api")` (token, caller headers, service discovery); its own retries and timeouts from a config section with `ServiceClientResilienceOptions` + `.WithResilienceFrom<TOptions>()` (e.g. Requests' `ApprovalsClient`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Http/` |
| Paged lists: `PagedResult<T>` and `PagingOptions` (section `Paging`, register with `AddValidatedOptions`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Application/Paging.cs` |
| `Permissions` (the 15 constants and `All`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Domain/Permissions.cs`, namespace `ProcureAi.BuildingBlocks.Domain` (so a service domain can validate against it) |
