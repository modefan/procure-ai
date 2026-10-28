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
   **Every service it calls over HTTP** (`AddProcureAiServiceClient("<name>-api")`, including Access for permissions) goes into
   `.CallsServices(accessApi, ...)`: the reference makes service discovery resolve `http://<name>-api`, the wait keeps the API
   from starting before them. Without it every call fails (a submit answered 503). Add the service to
   `Every_service_references_and_waits_for_the_services_it_calls`.
   With `CapturedTables` set, it also adds the database's own Debezium Server (see "Change data capture" below).
7. **Tests**: reference `tests/ProcureAi.Testing` for `ServiceInfraFixture` (derive from it: Postgres, RabbitMQ, Redis, the migrated database, the `cdc` exchange and Debezium on demand, plus `CdcListener` and `RabbitProbe`), `PostgresFixture`, `MongoFixture`, `RabbitFixture`, `RedisFixture`, `TestLimits`,
   `TestAuth`, `ServiceApiFactory<Program>` (derive from it to add hooks), `StubAccessServer`, `StubApprovalsServer` (Approvals' internal preview), `StubSuppliersServer` (Suppliers' internal item lookup; a default catalog for every tenant, settable per tenant), `RabbitRecorder` (records what a
   service publishes and sends) and `RabbitContainer` (a broker of its own to stop, for outage tests). Never copy a fixture.
8. **Gateway**: add the YARP route and cluster, and the service's OpenAPI document to the Swagger dropdown.

## A service on MongoDB (Suppliers, the Assistant's chat history)

For a service whose data is documents and that publishes no integration events. Everything is in `BuildingBlocks.Persistence.Mongo`
(only a service's Infrastructure and `Hosting` may use it; an architecture test enforces that).

- **`Service:Store`** (`ServiceOptions.Store`, default `Postgres`): set `MongoDB` through `ServiceSpec(..., Store: ServiceStore.MongoDB)`
  in the AppHost, which creates the database on the `mongo` resource (`mongo.AddDatabase`), passes its connection string and
  `Service__Store=MongoDB` to `-migrate` and `-api`, and adds **no Debezium** (it reads the Postgres WAL; `CapturedTables` must be
  empty). `ServiceHost` then adds a MongoDB readiness check (`ping`) instead of Postgres. `--migrate` runs every registered
  `IMongoMigration.ApplyAsync(IMongoDatabase, ct)` (indexes; idempotent), then the optional `migrate` callback (pass `null`), then the
  demo seed; there is no Wolverine message store to migrate. Register migrations as singletons.
- **Infrastructure**: `services.AddProcureAiMongo(configuration, "<name>db")` (one `IMongoClient` singleton, the `IMongoDatabase`, tracing),
  and `services.AddTenantCollection<T>("collection")` for each collection of tenant data. `T : ITenantDocument` (a mapped `TenantId`
  string).
- **`TenantCollection<T>` is the tenant boundary** (the MongoDB equivalent of the EF Core tenant query filter): the raw
  `IMongoCollection<T>` is not exposed; `Find`/`CountAsync`/`ReplaceOneAsync`/`DeleteOneAsync`/`DeleteManyAsync` AND `TenantId == caller's tenant`
  onto the caller's filter (never replace it), `InsertOneAsync`/`InsertManyAsync`/`ReplaceOneAsync` throw `ForbiddenException` for a
  document of another tenant (before writing anything), and a caller without a tenant gets `ForbiddenException` on every call. It
  offers no operator updates or aggregation, because those could move a document to another tenant: read, change, replace. Seeding and
  migrations work on the raw `IMongoDatabase` as the system, so a seeder that wants the same checks builds
  `new TenantCollection<T>(rawCollection, seedCaller)` with an `ICallerContext` for the tenant it seeds.
- **Wolverine without a message store**: `options.UseProcureAiAccessEviction(configuration, "<name>-api", handlersAssembly)` instead of
  `UseProcureAiMessaging`. RabbitMQ with caller propagation and only the queue `<name>-api-access-changed` (bound to `access`), which is
  **buffered** (no durable inbox, no outbox, no transactions). `AccessChanged` still advances the cache generation and evicts the
  tenant's tags; a lost message only leaves a cached answer until it expires or the generation is re-read (seconds).
  `AccessEvictionTests` proves it end to end (Access-side publish through RabbitMQ, generation advanced, tagged entries evicted, another
  tenant untouched). A service that later needs events of its own would need a message store, and so Postgres. `AccessEvictionRetryTests` shows the scheduled retry of a Redis failure (`RetryAccessChangedCacheFailures`) works without a durable store: it is an in-memory retry, lost if the process stops meanwhile, which only leaves a cached answer until it expires.
- **Suppliers is the first such service** (`src/Services/Suppliers`). Domain classes stay free of MongoDB: Infrastructure has a document class (`SupplierDocument : ITenantDocument`, Decimal128 price) mapped to and from the domain, and the Application layer sees only `ISupplierStore`. `ConfigureSuppliers` adds `AlwaysUseServiceLocationFor<ISupplierStore>()`, because the store is built from the factory-registered `TenantCollection<T>` and Wolverine otherwise refuses to generate its handlers. The api sets `Service:Store=MongoDB` in its own appsettings (so a bare `--migrate` run works too), and its csproj sets `OpenApiDatabaseConnectionString` to a `mongodb://` placeholder for the OpenAPI build step (the default `Host=...` placeholder is not a Mongo URL). Tests use `MongoFixture.ConnectionStringFor(db)`, which now authenticates against `admin`.
- **Tracing: the driver's own.** MongoDB.Driver 3.13 has OpenTelemetry tracing built in (`MongoClientSettings.TracingOptions`, on by
  default; activity source `MongoTelemetry.ActivitySourceName`, "MongoDB.Driver"), so `AddProcureAiMongo` only registers that source
  with the tracer provider (`AddSource`). The community package `MongoDB.Driver.Core.Extensions.DiagnosticSources` was not used: it is
  the pre-3.x way, adds a dependency and a second set of spans for the same commands.
- **Tests**: `MongoFixture` (`ProcureAi.Testing`) starts `mongo:8.0` as a single-node replica set like the AppHost, with the limits of
  `Resources:Mongo` (including the WiredTiger cache size), and fails at startup if it is not the replica set's PRIMARY.
  `Client` and `ConnectionStringFor(database)` give a client or a connection string.

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
  - **Interceptor rows are trusted by Debezium's metadata, not their own columns**: an `audit_entries` insert counts as the app's
    row only when `source.txId` (the transaction that really inserted it) equals its `transaction_id` column; its time is
    `source.ts_ms`. A row inserted by another transaction (a forgery copying a change's `transaction_id`) is stored as a data change
    on `audit_entries`, logged as a warning (IDs only) and flagged outside the app like any unmatched change; it never matches a
    change or withdraws an alert.
  - **Known limits**: (1) a forger who writes the data change *and* a fake `audit_entries` row in ONE transaction is not detected
    by matching. Cover it with pgaudit (database-role audit), and give only the app's database role INSERT on `audit_entries`.
    (2) Debezium skips truncates by default, so a psql `TRUNCATE` is never flagged as outside the app. To capture them set
    `skipped.operations=none` and handle op `t` (it names no row, so it needs its own alert).

## Memory of the whole system (measured 2026-10-28, plan 03 Task 12)

The whole AppHost (eight services, six Debezium Servers, the simulators, gateway, Angular dev server) at the end of a system test
run (`ApprovalFlowTests` and `SupplierOutageTests`, after `OrderToPaymentTests` in the run before), `docker stats --no-stream` and
the processes' working sets. Budget for Claude's work on the shared PC (CLAUDE.md): containers at most 8 GB / 8 CPUs, .NET about
3 GB. `ServiceResourcesTests.Container_caps_of_the_whole_system_stay_within_8_GB_and_8_CPUs` keeps the caps inside the budget.

| Containers | Used | Cap |
|---|---|---|
| keycloak | 634 MB | 768 MB |
| debezium-requestsdb / -accessdb / -budgetsdb / -approvalsdb / -purchasingdb / -invoicingdb | 288 + 289 + 293 + 294 + 301 + 295 MB | 6 x 512 MB, 0.4 CPU each |
| postgres | 268 MB | 768 MB |
| mongo | 244 MB | 512 MB |
| rabbitmq | 126 MB | 384 MB |
| redis, mailpit | 9 + 6 MB | 192 + 64 MB |
| **Total** | **about 3.0 GB** | **5.6 GB, 7.9 CPUs** |

| .NET processes (working set) | MB |
|---|---|
| invoicing-api | 662 |
| approvals-api | 562 |
| purchasing-api | 562 |
| budgets-api | 518 |
| audit-api | 508 |
| requests-api | 444 |
| access-api | 392 |
| suppliers-api | 348 |
| gateway | 107 |
| simulators | 93 |
| AppHost (here the system test host) | 198 |
| **Services, simulators, gateway and AppHost** | **about 4.4 GB** |
| plus Aspire's `dotnet run` launcher per project (11 x about 100 MB) | about 1.1 GB |
| plus DCP, Aspire's orchestrator (38 `dcp` processes, about 50 MB each) | about 1.9 GB |

Plus the Angular dev server (`ng serve`, Node) at about 0.6 GB. The services' GC heaps are capped (`Resources:DotNet`,
256 MB hard limit each); the rest of a working set is runtime, JIT code and native memory. The .NET total is over the ~3 GB of the
budget (plan 03 ruling 20: nine .NET processes; it was 2.8 GB with five services after plan 02); full-system runs are kept short.
Debezium stays at 512 MB / `-Xmx256m`; its CPU cap is 0.4 per instance so the six instances fit: 6 x 0.4 + 5.5 = 7.9 CPUs, and
6 x 512 MB + 2.6 GB = 5.6 GB of memory caps (plan 03 ruling 19; the cost is slower CDC catch-up under load).

## Business metrics

A service's own metrics go on a meter named `ProcureAi.<Service>` created through `IMeterFactory` (see Audit's `AuditMetrics`);
the service defaults export every `ProcureAi.*` meter over OTLP, so nothing else needs registering. Tests read them with a
`MeterListener`.

## Receiving webhooks

Signed webhooks (a supplier confirming an order, an invoice arriving) use the building block in `BuildingBlocks.Web/Webhooks`.

- Header `X-Procure-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>`. The signature covers the bytes exactly as received, so never re-serialise before checking. Tolerance 5 minutes either way, constant-time comparison.
- Configuration (`Webhooks` section): `Tolerance` (`00:05:00`), `MaxBodyBytes` (1 MB; more gets 413), and one secret per source, `Webhooks:Sources:<source>:Secret` (at least 32 characters; user secrets locally, a Kubernetes secret when deployed). The host refuses to start when a configured secret is too short or an action names a source with no secret.
- In the Api: `services.AddProcureAiWebhooks(builder.Configuration)`, then mark the action `[SignedWebhook("supplier")]`. The action is anonymous (the signature is the authentication; other actions keep their token requirement), reads `Request.Body` as usual (it is buffered) and never sees a request that failed verification. A failure is the same 401 for every cause; the cause goes to the warning `Webhook rejected {Source} {Reason}` and the counter `procure.webhooks.rejected` (tags `source`, `reason`: `missing`, `malformed`, `expired`, `bad_signature`, `too_large`) on meter `ProcureAi.Webhooks`.
- Rate limiting: the global limiter (`AddProcureAiRateLimiting`) already covers every endpoint, and an anonymous webhook falls into its client-IP partition, so a webhook action needs nothing extra. Tune the allowance in the `RateLimiting` section; a sender that legitimately bursts (a simulator) is a reason to raise `PermitLimit`, not to exempt the endpoint.
- Replay protection: the handler calls `IWebhookInbox.TryRecordAsync(source, eventId, ct)` (BuildingBlocks.Application) before applying the effect and commits it in the same `SaveChanges`. `false` means already processed: return 200 and change nothing. Two deliveries racing past the check collide on the key when saving; catch `DbUpdateException` and treat `WebhookInbox.IsDuplicate(ex)` (BuildingBlocks.Persistence) as already processed too. In the DbContext `modelBuilder.AddProcessedWebhooks()` (table `processed_webhooks`, key `(source, event_id)`), in DI `services.AddWebhookInbox<TDbContext>()`. A controller (which may not use EF Core) wraps its `InvokeAsync` in `WebhookInbox.ApplyOnceAsync(...)`, which answers false for that collision. Refuse an event ID longer than `WebhookLimits.MaxEventIdLength` (and any value longer than its column) with 400 before invoking: a database error would be a 500, which the sender retries forever. Purchasing's `SupplierWebhooksController` is the example. The entity is `[AuditExclude]` and not captured by Debezium, but a captured database still needs `processed_webhooks` in `Audit:Cdc:KeyColumns` (`CdcKeyColumnsTests` checks every table of the model).
- The tenant of a webhook always comes from the stored entity it names, never from the payload. When that entity is not known yet
  (Invoicing: an invoice that beats `PurchaseOrderCreated`), the effect is stored **without a tenant** (`Invoice.UnassignedTenant`,
  invisible to every tenant's query filter, no arrival number) and adopted when the entity arrives: the event handler that creates it
  gives the waiting rows its tenant, and a local `AttachWaitingInvoices` message (retried on `OrderViewMissingException`) covers the
  race where the webhook committed just after that handler looked.
- Tests sign requests with `WebhookTestSigner.Sign(request, secret, at)` (`tests/ProcureAi.Testing`).

## Where the pieces live

| Piece | Location |
|---|---|
| `ServiceHost`, `ServiceOptions` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Hosting` (references Web, Messaging, Persistence and Persistence.Mongo; only `*.Api` projects may use it) |
| MongoDB: `AddProcureAiMongo`, `TenantCollection<T>`, `ITenantDocument`, `IMongoMigration`, `MongoReadinessCheck` (see "A service on MongoDB") | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence.Mongo` |
| `ApplicationExceptionFilter` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Errors` |
| OpenAPI build step | `src/Services/Directory.Build.targets` |
| `AddProcureAiService`, `ServiceSpec`, `SharedResources` | `src/AppHost/ProcureAi.AppHost/ServiceResources.cs` |
| `EnableCdc` / `DisableCdc` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence/CdcMigrations.cs` |
| `AddDebeziumFor`, Debezium shared properties | `ServiceResources.cs`, `infra/debezium/application.properties` |
| Test fixtures, auth and factory | `tests/ProcureAi.Testing` (`ServiceInfraFixture` is the per-service infrastructure base) |
| Concurrency retries of message handlers | `ConcurrencyRetryOptions` (BuildingBlocks.Application, bound from the service's section) and `RetryConcurrencyConflicts` (BuildingBlocks.Messaging) |
| Retries of message handlers when a called service is down | `DependencyRetryOptions` (BuildingBlocks.Application, bound from the service's section) and `RetryTransientDependencyFailures` (BuildingBlocks.Messaging): scheduled retries, then dead letters |
| A client of someone else's system (supplier API, ERP): `AddExternalSystemClient<TClient, TImpl, TOptions>(configuration, section, name)` with options deriving from `ExternalSystemClientOptions` (base URL, `X-Api-Key`, attempt/total timeouts, retries with backoff and jitter, circuit breaker); `ExternalSystemClient.SendAsync` turns "no answer" (open circuit, timeout, unreachable) into the caller's own "try again later" exception, `RefusalMessageAsync` reads a 400/422 problem, `ToCents` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Http/ExternalSystemClient.cs` |
| The supplier/ERP simulator on a test server for a service's integration tests: `SimulatorHost` (a linked source file, it needs the `simulators` extern alias: `<Compile Include="../Shared/SimulatorHost.cs" />`), its webhooks through `WebhookTraffic` (`ProcureAi.Testing`) | `tests/Shared/SimulatorHost.cs` |
| A client of another service of ours: `AddProcureAiServiceClient<TClient, TImpl>("<name>-api")` (token, caller headers, service discovery); its own retries and timeouts from a config section with `ServiceClientResilienceOptions` + `.WithResilienceFrom<TOptions>()` (e.g. Requests' `ApprovalsClient`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Http/` |
| Human-readable numbers (PR-1043, PO-2007): `ITenantSequence.NextAsync(tenant, name)` (single-row upsert in the caller's transaction, sequential per tenant and name from 1). In the DbContext `modelBuilder.AddTenantSequences()` (table `tenant_sequences`, key `(tenant_id, name)`), in DI `services.AddTenantSequence<TDbContext>()`. Not captured by Debezium (counters are not business data), but a captured database still needs `tenant_sequences` in `Audit:Cdc:KeyColumns` (`CdcKeyColumnsTests` checks every table of the model) | `ITenantSequence` in BuildingBlocks.Application/Sequences, `TenantSequence<TDbContext>` in BuildingBlocks.Persistence/Sequences |
| Which requests and cost centers the caller may reach: `ICallerScope.GetAsync` gives a `CallerScope(All, UserId, CostCenterCodes)` with `Allows(requesterId, costCenterCode)`. The admin key and a service acting for nobody get `All`; a service on behalf of a user and every other caller get the cost centers Access assigns (finance approvers get every cost center there). Registered by `ServiceHost`; a test host that builds its own container calls `services.AddProcureAiCallerScope()` (a plain registration: Wolverine refuses factory lambdas as handler dependencies) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Application/Scope` |
| Exponential backoff with jitter: `BackoffOptions` (`InitialDelay`, `Multiplier`, `MaxDelay`, `JitterFraction`; `DelayFor(attempt, random)`). `ConcurrencyRetryOptions` and `DependencyRetryOptions` compute their delays through it (`.Backoff`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Application/Backoff` |
| Every event of an exchange has a handler: `HandlerCoverage.AssertEveryEventHandled(handlersAssembly, "ProcureAi.Contracts.<Ns>", ...)` in the service's unit tests (an event without a `Handle`/`Consume`/`StartOrHandle`/`NotFound`/`Start`/`Orchestrate` method in a `...Handler` class or a saga is listed). Add the namespace when the service binds a new exchange | `tests/ProcureAi.Testing/HandlerCoverage.cs` |
| Signed webhooks: `[SignedWebhook("source")]`, `WebhookSignature`, `IWebhookInbox` replay protection (see "Receiving webhooks") | `BuildingBlocks.Web/Webhooks`, `BuildingBlocks.Application/Webhooks`, `BuildingBlocks.Persistence/Webhooks` |
| Paged lists: `PagedResult<T>` and `PagingOptions` (section `Paging`, register with `AddValidatedOptions`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Application/Paging.cs` |
| `Permissions` (the 15 constants and `All`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Domain/Permissions.cs`, namespace `ProcureAi.BuildingBlocks.Domain` (so a service domain can validate against it) |
