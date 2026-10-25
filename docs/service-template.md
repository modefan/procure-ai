# Service template: adding a service

Every service is built the same way from shared pieces. Nothing is copied from another service.

## Checklist

1. **Projects** (`src/Services/<Name>/`): `ProcureAi.<Name>.Domain`, `.Application`, `.Infrastructure`, `.Api`, with the
   dependency rules of CLAUDE.md. Add them to `ProcureAi.slnx`; the architecture tests pick them up by name.
2. **Api composition root** (`<Name>Api.cs`): one call to `ServiceHost.Build(args, "<name>-api", builder => ...)` from
   `ProcureAi.BuildingBlocks.Hosting`. The callback adds only what is the service's own: its options
   (`AddValidatedOptions`), its infrastructure, and `builder.Host.UseWolverine(...)` (use `UseProcureAiMessaging` /
   `UseProcureAiTransport`). `ServiceHost` already adds web, caching, the exception filter (404 / 403 / 412 / 422), the
   caller model (`ServiceIdentity`, `ICallerContext`, `IncomingMessage`) and the readiness checks (Postgres, RabbitMQ, Redis).
3. **`Program.cs`**: `var app = <Name>Api.Build(args); await ServiceHost.RunAsync(app, args, migrate, seedDemoData);`
   `--migrate` runs the EF Core migration, Wolverine's message-store migration and (when `DemoSeed:Enabled=true`) the demo
   seed, then exits. Also `public partial class Program;` for the integration tests.
4. **`appsettings.json`**: copy the shared sections from Requests, and set `"Service": { "DatabaseConnectionName": "<name>db" }`.
5. **OpenAPI document**: in the Api csproj set `OpenApiDocumentName` (e.g. `<name>-v1`) and `OpenApiDatabaseConnectionName`.
   `src/Services/Directory.Build.targets` then writes `openapi/<name>-v1.json` on every build (commit it) and references the
   `Microsoft.Extensions.ApiDescription.Server` package; the project needs nothing else.
6. **AppHost**: `builder.AddProcureAiService<Projects.ProcureAi_<Name>_Api>(new ServiceSpec(...), shared)` in `AppHost.cs`
   creates the database, `<name>-migrate` and `<name>-api` with the environment, limits, wait-for-migration and `/health/ready`.
   Reference the returned API from the gateway. Add a `ServiceResourcesTests` case.
   With `CapturedTables` set, it also adds the database's own Debezium Server (see "Change data capture" below).
7. **Tests**: reference `tests/ProcureAi.Testing` for `PostgresFixture`, `RabbitFixture`, `RedisFixture`, `TestLimits`,
   `TestAuth` and `ServiceApiFactory<Program>` (derive from it to add hooks). Never copy a fixture.
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
- Aspire creates the databases; the Postgres init script only creates the `debezium` role. Debezium starts after the
  service's `-migrate` resource finished.
- Tests: `DebeziumFixture(network, postgres).StartAsync(database, tables...)` in `ProcureAi.Testing`, same mechanism.

## Where the pieces live

| Piece | Location |
|---|---|
| `ServiceHost`, `ServiceOptions` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Hosting` (references Web, Messaging and Persistence; only `*.Api` projects may use it) |
| `ApplicationExceptionFilter` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Errors` |
| OpenAPI build step | `src/Services/Directory.Build.targets` |
| `AddProcureAiService`, `ServiceSpec`, `SharedResources` | `src/AppHost/ProcureAi.AppHost/ServiceResources.cs` |
| `EnableCdc` / `DisableCdc` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence/CdcMigrations.cs` |
| `AddDebeziumFor`, Debezium shared properties | `ServiceResources.cs`, `infra/debezium/application.properties` |
| Test fixtures, auth and factory | `tests/ProcureAi.Testing` |
