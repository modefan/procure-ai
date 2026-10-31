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
   Display names of users: `IAccessDirectory.GetDisplayNameAsync` (cached like the rest; null for an unknown user); name and email
   address: `IAccessDirectory.GetUserAsync` (the same cached lookup of Access `internal/users/{userId}`; `Email` empty when unknown).
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
   `TestAuth`, `ServiceApiFactory<Program>` (derive from it to add hooks), `StubAccessServer`, `StubApprovalsServer` (Approvals' internal preview; for calls as the user also the user's preview and a request's chain), `StubSuppliersServer` (Suppliers' internal item lookup; a default catalog for every tenant, settable per tenant; the supplier list as the user), `StubRequestsServer` and `StubBudgetsServer` (a request by number and the budget list, called as the user; every call as the user is recorded as a `StubUserCall` with the relayed `Authorization`, so a test can assert it), `MailpitFixture` (the mail catcher, read through its HTTP API), `RabbitRecorder` (records what a
   service publishes and sends) and `RabbitContainer` (a broker of its own to stop, for outage tests). Never copy a fixture.
8. **Gateway**: add the YARP route and cluster, and the service's OpenAPI document to the Swagger dropdown.

## A service on MongoDB (Suppliers; the Assistant's chat database, see below)

For a service whose data is documents and that publishes no integration events. Everything is in `BuildingBlocks.Persistence.Mongo`
(only a service's Infrastructure and `Hosting` may use it; an architecture test enforces that).

- **`Service:Store`** (`ServiceOptions.Store`, default `Postgres`): set `MongoDB` through `ServiceSpec(..., Store: ServiceStore.MongoDB)`
  in the AppHost, which creates the database on the `mongo` resource (`mongo.AddDatabase`), passes its connection string and
  `Service__Store=MongoDB` to `-migrate` and `-api`. Its `CapturedTables` are **collections** for the one MongoDB Debezium
  (`debezium-mongo`, see "Change data capture: MongoDB" below); empty for none. `ServiceHost` then adds a MongoDB readiness check (`ping`) instead of Postgres. `--migrate` runs every registered
  `IMongoMigration.ApplyAsync(IMongoDatabase, ct)` (indexes; idempotent), then the optional `migrate` callback (pass `null`), then the
  demo seed; there is no Wolverine message store to migrate. Register migrations as singletons.
- **Infrastructure**: `services.AddProcureAiMongo(configuration, "<name>db")` (one `IMongoClient` singleton, the `IMongoDatabase`, tracing),
  and `services.AddTenantCollection<T>("collection")` for each collection of tenant data. `T : ITenantDocument` (a mapped `TenantId`
  string). A captured collection's document class also implements `IAuditedDocument` (`[BsonElement(AuditStamp.ElementName)]
  [BsonIgnoreIfNull] public AuditStamp? Audit`), so `TenantCollection` stamps every write (see below).
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

## A service with Postgres and a second MongoDB database (the Assistant)

For a service whose main data is relational (and that publishes or consumes events, so it needs the outbox and inbox) but that also keeps
documents (plan 05 ruling 4: the Assistant's chat history and invoice scans). Postgres stays the main store with everything above
(EF Core, Wolverine's message store, Debezium); MongoDB is a second database next to it.

- **AppHost**: `ServiceSpec(..., Store: ServiceStore.Postgres, SecondaryMongoDatabase: "<name>chatdb")`. `AddProcureAiService` then also
  creates that database on the `mongo` resource and gives `-migrate` and `-api` its connection string, a wait for it and
  `Service__SecondaryMongoConnectionName=<name>chatdb`. Only for a Postgres service (anything else throws). Debezium captures the Postgres
  tables as usual; `SecondaryMongoCapturedCollections: ["conversations"]` adds collections of the second database to `debezium-mongo`.
- **`Service:SecondaryMongoConnectionName`** (`ServiceOptions`): also set it in the api's `appsettings.json`, so a bare `--migrate` run works.
  `ServiceHost` then adds the MongoDB readiness check (`mongodb`, a ping) next to `postgres`, and `--migrate` runs every registered
  `IMongoMigration` against it first, then the EF Core migrations, Wolverine's message store and the seed.
- **Infrastructure**: `services.AddProcureAiMongo(configuration, "<name>chatdb")` (the one `IMongoClient` and `IMongoDatabase` of the
  service; the Postgres side never registers either) and `AddTenantCollection<T>` per collection, exactly as in "A service on MongoDB".
- **OpenAPI build step**: `OpenApiDatabaseConnectionName` is the Postgres database as usual; add the Mongo placeholder through
  `OpenApiExtraSettings`: `ConnectionStrings__<name>chatdb=mongodb://openapi.invalid/<name>chatdb`.
- **Tests**: derive from `ServiceInfraFixture` and override `UsesMongo => true`: a `MongoFixture` (replica set) starts with Postgres, RabbitMQ
  and Redis, and `MongoConnectionStringFor(database)` gives its connection string. Pass it to `ApiMigrateProcess.RunAsync(..., environment:
  { ["ConnectionStrings__<name>chatdb"] = ... })` and to the factory (`UseSetting`).
- **Domain classes stay free of persistence**: a table that must not get audit rows but whose class lives in a Domain project (which cannot
  carry `[AuditExclude]`, a Persistence type) is excluded in its EF configuration with `builder.ExcludeFromAudit()` (BuildingBlocks.Persistence,
  `AuditExclusion`). The Assistant does this for `chunks` and `policy_document_files`. A captured database still needs every table of its
  model in Audit's `Audit:Cdc:KeyColumns` when the key is not `id` (`CdcKeyColumnsTests` lists the Assistant too).
- **pgvector**: in the custom Postgres image already. The model calls `HasPostgresExtension("vector")`, so the first migration creates the
  extension, and the context uses `UseNpgsql(..., npgsql => npgsql.UseVector())` (Pgvector.EntityFrameworkCore 0.3.0, which works with
  EF Core 10.0.13 and Npgsql 10.0.3). The Domain keeps embeddings as `float[]`; the configuration converts them to the `vector(1536)` column.

### The Assistant: AI providers and pipelines

- `AI` section (`AiOptions`, validated at startup): `Provider` (`Fake` | `OpenAI`), `Endpoint` and `ApiKey` (both required for `OpenAI`, the
  key from user secrets or `AI__ApiKey`), `Chat { Model, MaxOutputTokens 800, Timeout 60 s }`, `Embedding { Model, Dimensions }` (must be
  1536, the column), `Guard { Model? }`, `Telemetry { LogContent }` (true only in Development), `Prices` (USD per million tokens, for the
  cost metric), `Fake { StreamDelayMs 15 }`. `Fake` is the default in appsettings: no key, no account. `OpenAI` covers any OpenAI-compatible
  endpoint (Azure Foundry's `/openai/v1`, OpenAI).
- `AiSetup.AddAssistantAi` (Assistant.Infrastructure, the only project that may name a provider SDK:
  `Only_assistant_infrastructure_references_provider_sdks`) registers keyed `IChatClient`s `answer` (an `IAnswerGuardrail` when one is
  registered, then default limits, function invocation (at most `Assistant:Tools:MaxCallsPerQuestion` rounds), OpenTelemetry, logging,
  provider) and `utility` (default limits, OpenTelemetry, logging, provider), and one `IEmbeddingGenerator<string, Embedding<float>>`
  (OpenTelemetry, logging, provider). Spans go to the activity source `ProcureAi.Assistant.AI` (every `ProcureAi.*` source is traced by
  the service defaults; AiSetup registers none). The logging stage is `ModelCallLogging`: one content-free line per call (model,
  tokens, duration, outcome). Prompt and answer text reach spans (`EnableSensitiveData`) and logs (Microsoft.Extensions.AI's
  `LoggingChatClient` / `LoggingEmbeddingGenerator`, added only then) only with `LogContent`: the option is the gate, not the log level.
- The first line of a system message names the task: `#task: answer|rewrite|context|classify|scan` (`AiTasks`). The Fake model routes on
  it (`FakeChatClient`, `FakeIntentRules`, `FakeEmbeddingGenerator`: hashed bag of words and bigrams, 1536 buckets, L2-normalised).
  The Fake provider reports its own model names (`fake-chat`, `fake-embedding-1536`), so its vectors are never taken for a real model's.
- AppHost: the `AI:*` settings of the AppHost's configuration (user secrets) are passed as `AI__*` only when set (`AiSettings`, the key as a
  secret parameter); nothing is required. Metrics: meter `ProcureAi.Assistant` (`AssistantMetrics`).

### The Assistant: policy documents and ingestion

- API `api/v1/assistant/policy-documents` (`admin.policies`): `POST` multipart `file` + `title?` (`[Idempotent]`, 202), `PUT {id}/file`
  (new version, `If-Match`), `DELETE {id}` (soft remove, `If-Match`), `GET` (list, ETag = body hash), `GET {id}` (ETag = `rowVersion`).
  PDF or Markdown only, checked from the content (`PolicyFileType`: `%PDF-`, or strict UTF-8 without NUL): else 415; over
  `Assistant:Uploads:MaxBytes` (10 MB): 413.
- Ingestion is the durable local message `IngestPolicyDocument(DocumentId, Version)` on `assistant-ingestion`. Its handler takes no
  DbContext (so Wolverine opens no transaction around it): `PolicyIngestion` commits step by step through `IAssistantUnitOfWorkFactory`
  (a scope per unit, `IDbContextOutbox<AssistantDbContext>`, an explicit transaction so `ExecuteDeleteAsync`, the changes and the events
  commit together). Steps: extract, `IPromptShield.CheckDocumentAsync` (allow-all until Task 4), plan, reuse by
  `(child, content_hash, embedding_model)` in the tenant (a reused chunk copies the context and the vector of the earlier chunk with the
  same text hash; never across tenants), context (`utility`, `#task: context`) and batched embeddings, progress per
  batch (`PolicyDocumentProgressed` per new 10 % step), then the switch (older versions' chunks deleted and `current_version` set in one
  transaction). `SearchableChunks.Of(db)` is what retrieval may read: purchase chunks, and policy chunks of the current version of a
  document that was not removed.
- The document guard reads the title, every heading path and the text. The document row is written only per 10 % step (and status
  changes), not per batch. Uploads over `MaxBytes` + 64 KB are refused (413) before the body is read (`[UploadSizeLimit]`, also lowers
  Kestrel's body limit). An unexpected failure (a bug, a lost race after its retries) ends the version `Failed` ("Ingestion failed
  unexpectedly.") instead of dead-lettering; a passing database failure keeps the shared scheduled retry.
- A stale job (the document was replaced again or removed) stops at its next commit and deletes what it stored. Provider failures
  (`IAiFailures`: transient HTTP, `ClientResultException` 429/5xx, timeouts) become `AiProviderUnavailableException`, retried on the durable
  schedule `Assistant:Ingestion:RetryDelays` (one comma-separated string, default `00:00:10, 00:01:00, 00:05:00`); on the last attempt
  (`Envelope.Attempts`) the job marks the version `Failed` itself. A retry resumes: stored children are kept.
- Demo seed (`--migrate` with `DemoSeed:Enabled`): `infra/seed/procurement-policy.md` (embedded in Infrastructure) for `acme` and
  `globex`, fixed IDs. The migrate run never starts Wolverine, so the seed writes the `IngestPolicyDocument` envelope straight into
  `wolverine_incoming_envelopes` (Wolverine's EF `IncomingMessage`, status Incoming, owner 0, destination `local://assistant-ingestion`)
  in the same `SaveChanges` as the document; the running api's durability agent recovers and ingests it.

### The Assistant: conversations and the answer stream

- API `api/v1/assistant` (`assistant.use`, tenant; the owner is `ICallerContext.UserId`, so the admin key acts for `X-On-Behalf-Of-User`):
  `GET conversations` (newest 20), `POST conversations` (`[Idempotent]`, 201 `{ id }`), `GET`/`DELETE conversations/{id}`, `GET quota`
  (`{ used, limit }`), and `POST conversations/{id}/messages` `{ text (1–2000), mode: new | regenerate | edit }` → `text/event-stream`.
  Admin (`admin.system`, ruling 18): `GET admin/conversations?userId&page&pageSize`, `GET admin/conversations/{id}` (also guard flags and
  timings). Another user's or tenant's conversation is a 404, the same as an unknown ID. GETs are cached 1 s under the tags of
  `ConversationCacheTag` (conversation, tenant list, tenant quota), all dropped on every change.
- Storage: one MongoDB document per conversation in `assistantchatdb.conversations` (`ConversationDocument`, messages embedded, string IDs,
  UTC dates), through `TenantCollection<T>`; the owner filter is added by `MongoConversationStore`. Index `(TenantId, UserId, UpdatedAt desc)`
  (`ConversationIndexMigration`, run by `--migrate`). At most `Conversation.MaxMessages` (200) messages.
- The stream (`AnswerStreamer`, ruling 6): a controller action returning `TypedResults.ServerSentEvents` (no `[Idempotent]`, no `[CachedGet]`:
  both buffer). Before the stream: 404, 422 (nothing to regenerate or edit, or the conversation is full), 409 (the per-conversation Redis lock
  `assistant:answer:{tenant}:{id}`, a DistributedLock lease of `Assistant:Answer:LockLease` 30 s, extended while held), 429 (the quota,
  `Retry-After`), 400 (validation). Then: rewrite a follow-up with the last `Assistant:Answer:HistoryMessages` (6) messages (`utility`,
  `#task: rewrite`), hybrid search (top `Assistant:Retrieval:TopK`), `sources`, the answer (`PromptBuilder`: sources in `<data>` blocks in the
  system message, history next, the user's own question last), `delta`s, the grounding rule (`replace` with `AnswerStreamer.NotGroundedText`),
  save, `done`. Failures after the start are an `error` event: 422 guard, 503 provider unavailable or timed out, 500 anything else. The answer
  is saved and the lock released in the iterator's `finally` (also registered for the end of the request), so a client that leaves gets its
  partial text saved as `Stopped` (up to `HoldBackChars` of held-back text is lost, ruling of Task 4). The lock is released right after a successful save, before `done`; `done` only for a saved answer (else an `error` 500 "The answer could not be saved."). History keeps only question-answer pairs whose answer is Complete, or Stopped after the input guard passed the question (`Message.InputChecked`, marked by the guard through an `InputCheck` in `ChatOptions.AdditionalProperties["inputCheck"]`, which it never passes on); a blocked or unchecked question never reaches the models again, and earlier answers lose their `[S<n>]` markers. Event names and data: `SseEvent`.
- Quota (ruling 7): `RedisQuestionQuota`, a Lua check-and-increment per tenant, user and calendar day of `Assistant:Quota:TimeZone`
  (Africa/Johannesburg) on the injected `TimeProvider`; a question counts when its answer starts (also when a guard blocks it).
- Provider timeout: every chat pipeline ends in `ProviderTimeoutChatClient` (innermost): a call may take `AI:Chat:Timeout` in all, a stream
  may wait that long for each next update, then a `TimeoutException` (transient).
- Gateway: the `assistant` cluster has `HttpRequest { ActivityTimeout 00:05:00, AllowResponseBuffering false }`, so long streams are not cut
  off and events are flushed as written (`Assistant_stream_is_not_buffered`).

## A service without a database (Notifications)

For a service that holds no business data of its own (Notifications: live pushes, presence, bell items in Redis). Plan 04 ruling 1.

- **`Service:Store=None`** (`ServiceStore.None`, in both `ProcureAi.BuildingBlocks.Hosting` and the AppHost's copy of the enum): set it in the
  api's `appsettings.json` and leave out `Service:DatabaseConnectionName` (required only for `Postgres` and `MongoDB`, checked at startup).
  `ServiceHost` then adds no database readiness check (RabbitMQ and Redis stay), and `--migrate` has nothing to run but the optional
  callbacks (there is no message store).
- **AppHost**: `ServiceSpec(..., Database: "", CapturedTables: [], ..., Store: ServiceStore.None)`. `AddProcureAiService` adds no database
  and no `<name>-migrate` resource (the api waits for nothing to complete), no Debezium (a `None` service with `CapturedTables` throws), and
  passes `Service__Store=None`. RabbitMQ, Redis, Keycloak, the admin key hash, the internal client and the .NET caps are wired as for every
  service; `.CallsServices(accessApi)` as usual (the Access client is still registered for every service).
- **OpenAPI build step**: leave `OpenApiDatabaseConnectionName` out of the api csproj; `Directory.Build.targets` then sets no database placeholders.
- **Wolverine without a message store**: `UseProcureAiAccessEviction` (the buffered `<name>-api-access-changed` queue) plus the service's own
  **buffered** listeners: no durable inbox, no outbox, no transactions. A message redelivered after a crash is handled twice, so every
  handler must be harmless to repeat (a second push; emails are deduplicated by message ID in Redis, Task 1b). A failure no rule matches goes
  to the error queue at once (Wolverine 6.53), so give each expected failure an explicit rule (Notifications: `RedisException` /
  `RedisTimeoutException` retried inline with `Notifications:PushRetry`: attempts, initial delay, multiplier, maximum delay; scalars,
  because an array in configuration is appended to the defaults instead of replacing them).
- **Tests**: `ServiceApiFactory<Program>(null, null, rabbit, redis)` (no database connection string, no Npgsql pool to clear).

### Notifications: the live hub

- Hub `LiveHub` at `/hubs/live` (`LiveHubContract.Path`), authorization policy `live-hub`: a signed-in caller **with a tenant** (the token's
  `tenant` claim; `X-Tenant-Id` only with the admin key or a service account). No tenant: 403 at negotiate; no credentials: 401. On connect
  the connection joins `t:{tenantId}` and `u:{tenantId}:{userId}` (`LiveGroups`, Domain); the client cannot name a group. Transports:
  WebSockets (through the gateway) and long polling (the in-memory test server).
- Server to client method `EntityChanged` (`LiveHubContract.EntityChangedMethod`) with `EntityChanged(entity, id, requestId, eventType,
  occurredAt)` (`ProcureAi.Contracts.Notifications`), camelCase JSON, IDs only (ruling 2): clients refetch through the authorized APIs.
  `entity` is `request | approval | budget | order | invoice | policy-document`; `id` is the request ID for `request` and `approval` (the approval chain of
  that request), the cost center code for `budget`, the order ID, the invoice ID and the policy document ID (no `requestId`).
- Feed: the buffered queue `notifications-api-live`, bound to `requests`, `approvals`, `budgets`, `purchasing`, `invoicing` and `assistant`. One handler
  method per event type (`...EventsHandler`, Application), each sending the pushes of `LiveEventMap.For(event)` to the event's tenant group,
  one push per entity, the most specific first and the request last. `HandlerCoverageTests` and `EventMapTests` fail when an event is
  added to one of those namespaces without a handler and a mapping.
- Scale-out: the SignalR **Redis backplane** (`AddStackExchangeRedis` on `ConnectionStrings:Redis`, channel prefix
  `Notifications:SignalR:ChannelPrefix`, default `procure-signalr`), so a push from the instance that took the message reaches clients on
  every instance.
- A client's `start()` completes with the handshake, **before** `OnConnectedAsync` has joined the groups: a push sent in that moment is not
  delivered to it. Clients revalidate what they hold after every (re)connect; the integration tests wait for a probe push before publishing.
- Gateway: routes `notifications-api` (`/api/v1/notifications/{**catch-all}`), `notifications-openapi` and `notifications-hub`
  (`/hubs/{**catch-all}`, WebSockets proxied by YARP, the session's access token added like on every API route; the negotiate POST needs
  `X-XSRF-TOKEN`; the WebSocket upgrade is a GET, so `HubOriginMiddleware` refuses (403) an upgrade whose `Origin` is neither the
  gateway's own nor in `Hubs:AllowedOrigins`). The hub policy also refuses a caller without a `sub` (no shared `anonymous` user
  group) and evaluates hub methods' `HubInvocationContext` from the connection's request.

### Notifications: presence, bell and email

- **Presence** (ruling 11). Hub methods `Heartbeat()`, `Watch(entity, id)` (returns the current `Viewer[]`) and `Unwatch(entity, id)`, each
  `[Authorize(Policy = "live-hub")]`; the tenant and user come from the connection's request (`LiveHubPolicy.Caller`), never from arguments.
  `entity` must be one of `LiveEntities`, `id` 1 to 64 of `[A-Za-z0-9._-]` (`PresenceTarget.TryCreate`, no `:`), at most
  `MaxWatchesPerConnection` per connection; anything else is a `HubException`. Watching joins the group `v:{tenant}:{entity}:{id}`.
  Server to client: `ViewersChanged(entity, id, Viewer[])` to that group and `OnlineChanged(Viewer[])` to `t:{tenant}`, with
  `Viewer { userId, name }`: distinct users (a second tab counts once), sorted by name; the name is the token's `name` claim, else the user ID.
- State in Redis (`RedisPresenceStore`), shared by all instances: sorted sets `presence:{tenant}:online` and
  `presence:{tenant}:view:{entity}:{id}` (member connection ID, score last heartbeat), hash `presence:connections` (connection → tenant, user,
  name), set `presence:conn:{id}:views`, set `presence:tenants`. Readers ignore entries older than the TTL. `PresenceSweeper` (hosted service on
  every instance) runs every `SweepInterval` under the DistributedLock `notifications:presence-sweep` (waits up to `SweepLockWait`), drops
  connections of **any** instance without a heartbeat for longer than the TTL and sends the changed lists through the backplane. Each
  removal is a Lua script that re-checks the heartbeat (one that arrived after the stale read keeps the connection) and keeps the
  connection's watch list, so its next heartbeat makes it online and a viewer again (lists re-sent). Forgetting an empty tenant is
  atomic too. A presence failure on connect is logged, not fatal: the next heartbeat records the connection. Options
  `Notifications:Presence { HeartbeatInterval 15 s, Ttl 45 s (≥ 2 × heartbeat, checked at startup), SweepInterval 15 s, SweepLockWait 5 s,
  MaxWatchesPerConnection 20 }`. Its clock is the keyed `TimeProvider` `PresenceClock.Key` (tests drive it without freezing HTTP timeouts).
- **Bell** (ruling 8). `RecipientRules`: `ApprovalRequested`, `ApprovalReminderDue` → the approvers due (`ApprovalEscalated` only pushes
  live: the new step's `ApprovalRequested` tells the new approver, so one escalation is one bell item and one email);
  `RequestApproved`, `RequestRejected`, `RequestCompleted`, `RequestCancellationRefused` → the requester (`RequesterId`, an optional contract
  property added in plan 04; older messages without it notify nobody). The approval event handlers call `IPersonalNotifier` after the live
  push: one `BellItem { id, text, requestId, link, createdAt, read }` per recipient (ID = the message ID, so a redelivery adds nothing),
  pushed as `NotificationAdded` to `u:{tenant}:{user}` whenever the user has the item after the add (again on a redelivery or retry:
  clients key items by ID; an item trimmed away at once is not pushed). Redis `bell:{tenant}:{user}:index|items|read` (Lua add: last `MaxItems` 50 within
  `Retention` 30 days, `Notifications:Bell`). API: `GET api/v1/notifications` → `{ items, unread }` (`[CachedGet]`, tag
  `notifications:{tenant}`, evicted on every write and new item), `POST api/v1/notifications/{id}/read` (404 for an item the caller does not
  have) and `POST api/v1/notifications/read-all`, both `[Idempotent]`, 204. The owner is the token's user (`X-On-Behalf-Of-User` with the key).
- **Email** (ruling 9). MailKit over SMTP (`Notifications:Smtp { Host, Port, From, FromName, AppBaseUrl, DedupWindow 7 d, SendingClaimTtl 2 min }`; the AppHost points it at
  Mailpit's `smtp` endpoint and the gateway's https endpoint), plain text plus simple HTML with the link `{AppBaseUrl}/requests/{id}`. Address
  from Access `internal/users/{userId}` (`Email`); none → bell item only. Once per (message, recipient): `SET email:{messageId}:{userId} NX`
  for `SendingClaimTtl` (2 min) while sending, then kept for `DedupWindow` once sent; released when the send fails. An instance that dies
  before sending leaves only the short claim, so RabbitMQ's redelivery still sends. The message ID is the AMQP `message-id` (Wolverine's
  envelope ID), the same on every delivery. A failed email throws `EmailDeliveryException`, retried inline by `RetryEmailFailures`
  (the `PushRetry` delays) and then dead-lettered; the retry sends only the emails that were not sent. Tests: `MailpitFixture`
  (`ProcureAi.Testing`, same image and caps) read through `GET /api/v1/messages`.

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
- **The Audit service consumes it** (queue `audit-cdc`, bound with `*.public.*` to `cdc` only, so the MongoDB keys never reach it; Wolverine reads Debezium's plain JSON as
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

## Change data capture: MongoDB (plan 06 ruling 16)

- **One Debezium Server for every captured collection**, `debezium-mongo`: one MongoDB connector watches the whole replica set, so unlike
  Postgres one instance serves Suppliers (`suppliersdb.suppliers`) and the Assistant (`assistantchatdb.conversations`). `AddProcureAiService`
  calls `AddDebeziumForMongo(collections, migrate, shared)` for a MongoDB service's `CapturedTables` and for
  `SecondaryMongoCapturedCollections`; the first call creates the container, later calls add their collections and a wait for their
  migration. It runs `infra/debezium/application-mongo.properties` with the same caps as the Postgres ones (`Resources:Debezium`: 512 MB,
  0.3 CPU, `-Xmx256m`; with eight Debezium Servers the 0.35 CPU of before would pass the 8-CPU budget). Environment:
  `DEBEZIUM_SOURCE_MONGODB_CONNECTION_STRING` (root user, `mongo:27017`, `replicaSet=rs0&directConnection=true`: Aspire's single-member set
  is initiated with an address only it can reach) and `DEBEZIUM_SOURCE_COLLECTION_INCLUDE_LIST`. Routing keys are
  `mongo.<database>.<collection>`; the APIs wait for it to be healthy (no data snapshot, as for Postgres).
- **No transaction ID to match, so every write is stamped**: `TenantCollection<T>` sets `_audit: { userId, correlationId, at }`
  (`AuditStamp`, BuildingBlocks.Persistence.Mongo) on every insert and replace of an `IAuditedDocument`, and before a delete it stamps
  the documents `deleting: true` (a delete event carries no document) and then deletes only the documents carrying that stamp (a document
  inserted in between is left alone). Always `$set` the whole `_audit` object: Audit reads an update's stamp from its `updatedFields`. For updates with operators use `AuditStamp.Apply(update, caller,
  correlationId, at)`. A write around `TenantCollection` (raw collection, mongosh) carries no new stamp.
- **A delete event has no document, only its key**: the transform `HeaderFrom$Key` copies the key into the AMQP header `documentKey`
  (the RabbitMQ sink publishes only the value).
- **Audit** (queue `audit-cdc-mongo`, bound with `mongo.#`, sequential; `MongoChangeParser` turns the extended JSON into plain JSON, so
  dates, decimals and object IDs read as text) decides each change at once from the stamp and the document's previous change
  (`MongoChange.ToDataChange`): **App** with the stamp's user when the change wrote a stamp within `Audit:Cdc:MongoStampWindow` (30 s) of
  its cluster time that is not the stamp the document already had (a mongosh replace that copies the document copies the old stamp); a
  delete is the app's when the previous change was the app's `deleting` stamp. Anything else is **OutsideApp** with an alert straight away
  (no grace period: there is nothing to wait for). Old values come from the previous change, so the log shows old -> new; `TransactionId`
  is null. `Audit:Cdc:MaskedCollections` keeps field names only (values `***`); it defaults to `assistantchatdb.conversations` and an
  empty list fails at startup (masking fails closed).
- **An update's `after` is a lookup** (`capture.mode=change_streams_update_full`): made when Debezium reads the event, so a lagging
  Debezium sees later values or, after a delete, nothing. Audit therefore reads an update's stamp from `updatedFields._audit` and builds
  its new values from the previous change's document plus `updatedFields`/`removedFields`; the lookup is used only without a previous
  change (or for a path that does not fit it). Covered by `Bulk_delete_read_late_is_attributed_to_the_app` (Debezium paused during a
  20-document delete).
- **Known limits**: someone with database access can forge a stamp (`$set: {"_audit.at": new Date()}`); pgaudit does not cover MongoDB.
  A change of a document whose earlier changes were never captured has no old values. Several Audit instances may decide two changes of
  one document in parallel (the queue is sequential per instance only).
- Tests: `ServiceInfraFixture.EnsureMongoCdcAsync(collections...)` starts the replica set (on the test network as `mongo`) and
  `MongoDebeziumFixture`, which returns once the change stream is open on the server; `MongoFixture.MongoshAsync` changes data outside
  the app. The parser tests run on real Debezium output (`tests/ProcureAi.Audit.Tests/Fixtures/mongo`).

## Demo reset (plan 06 ruling 4)

The public demo is reset every night at 02:00 Africa/Johannesburg. Every service takes part:

- **Endpoint**: `app.MapProcureAiDemoReset("api/v1/<name>")` in `<Name>Api.Build` (BuildingBlocks.Web `Demo/`) maps
  `POST api/v1/<name>/internal/demo/reset` → 204. Only when `DemoReset:Enabled` is true (off by default; the AppHost sets
  `DemoReset__Enabled=true` on every api): otherwise the route does not exist and every caller gets 404. `InternalPolicy` (a service
  account or the admin key), never a user token, not even an admin's. Not in the OpenAPI document. It runs the service's `IDemoResetter`,
  then clears the service's whole FusionCache (`ClearAsync(allowFailSafe: false)`: memory, Redis and the other instances through the
  backplane), which also removes the stored `[Idempotent]` results (else a re-seed with the same Idempotency-Keys would get the old 201 of a
  request that no longer exists).
- **`IDemoResetter`** (one per service, in its Infrastructure, registered scoped): deletes the service's business data of every tenant,
  then writes its own demo seed again. Running it twice must be safe. The pieces:
  - `WolverineDemoReset.ClearMessageStoreAsync(runtime)`: every envelope (incoming, outgoing, scheduled saga timeouts, dead letters).
  - `EfDemoReset.DeleteAllRowsAsync(db, keep?)`: loads every row of every root entity type (no query filters) and deletes it with ordinary
    tracked deletes in one `SaveChanges`, so the audit interceptor writes one Delete row per deleted row in the same transaction and Audit
    matches every Debezium delete to the app (ruling 5: a reset is never "changed outside the app"). Owned types go with their owner. It
    keeps `audit_entries` (deleting one would be a captured change without an interceptor row) and Wolverine's tables, and empties
    `tenant_sequences` (not captured), so numbers start again at PR-0001 and PO-1001. Never use `ExecuteDelete` or `TRUNCATE` on a captured table.
  - `MongoDemoReset.DeleteAllAsync(collection)`: tenant by tenant through `TenantCollection`, so each delete is stamped "deleting" first.
    Never `deleteMany` on the raw collection or drop it.
  - `RedisDemoReset.DeleteKeysAsync(redis, "prefix:*")`: plain Redis state (Notifications' bell, the Assistant's question quotas).
  - A service with a seed of its own reseeds in the same transaction when it can (Access, Budgets: no moment without a matrix or a budget).
  - Per service: Access keeps the tenants and the users who registered themselves into the Demo company, and publishes
    `AccessChanged(Matrix)` per tenant afterwards; Audit (called last) first waits until its CDC and event queues and its inbox have been empty for `DemoReset:QuietPeriod` (3 s; at most `DemoReset:DrainTimeout`, 2 min, else 503 and the caller retries: clearing in the middle of a reset transaction's 600 changes left the later half without the interceptor rows of the earlier half, false alerts), then deletes every Marten document and purges the broker queue
    `wolverine-dead-letter-queue`. The simulators have `POST api/v1/simulators/reset` (admin key): orders, postings, waiting webhooks,
    number sequences and chaos back to the start.
- **The caller**: `ProcureAi.DemoData reset` (simulators, then every service in parallel through the gateway with the admin key, then Audit
  after `DemoData:AuditGrace`, then `seed`; each call retried `ResetRetries` times; a failure exits 1 before Audit, and the next run starts
  from the beginning). `schedule` runs it at `DemoReset:TimeOfDay` in `DemoReset:TimeZone`. Locally the dashboard command "Reset demo data"
  on `demo-data` starts the resource once with `reset`.
- **A new service**: add its resetter, map the endpoint, add its name to `ResetRunner.ParallelServices`, and a
  `Demo_reset_clears_business_rows_and_reseeds` test (on a database of its own: the reset deletes every tenant's data).
- **Accepted limit**: a message still on the broker at 02:00 that refers to deleted rows fails and is dead-lettered.

## Memory of the whole system (measured 2026-10-28, plan 03 Task 12)

The whole AppHost (eight services, six Debezium Servers, the simulators, gateway, Angular dev server) at the end of a system test
run (`ApprovalFlowTests` and `SupplierOutageTests`, after `OrderToPaymentTests` in the run before), `docker stats --no-stream` and
the processes' working sets. Budget for Claude's work on the shared PC (CLAUDE.md): containers at most 8 GB / 8 CPUs, .NET about
3 GB. `ServiceResourcesTests.Container_caps_of_the_whole_system_stay_within_8_GB_and_8_CPUs` keeps the caps inside the budget.

| Containers | Used | Cap |
|---|---|---|
| keycloak | 634 MB | 768 MB |
| debezium-requestsdb / -accessdb / -budgetsdb / -approvalsdb / -purchasingdb / -invoicingdb | 288 + 289 + 293 + 294 + 301 + 295 MB | 6 x 512 MB, 0.4 CPU each (0.35 since plan 05) |
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
6 x 512 MB + 2.6 GB = 5.6 GB of memory caps (plan 03 ruling 19; the cost is slower CDC catch-up under load). Plan 05 adds a seventh
(`debezium-assistantdb`) and lowers the cap to 0.35 per instance: 7 x 0.35 + 5.5 = 7.95 CPUs, and 7 x 512 MB + 2.6 GB = 6.1 GB of memory
caps (plan 05 ruling 4; not measured yet). Plan 06 adds an eighth (`debezium-mongo`, ruling 16) and lowers the cap to 0.3 per instance:
8 x 0.3 + 5.5 = 7.9 CPUs, and 8 x 512 MB + 2.6 GB = 6.6 GB of memory caps.

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

## Calling another service as the user

Use this when a call must carry the **user's** permissions, not the service's: the Assistant's model tools ("what is the status of PR-1043?") must see exactly what the signed-in user may see.

- Register the client with `services.AddProcureAiUserClient<TClient, TImpl>("<name>-api")` (BuildingBlocks.Web/Http), then `.WithResilienceFrom<TOptions>()` for its own retries and timeouts as with `AddProcureAiServiceClient`. The host needs a `ServiceIdentity`.
- `UserTokenRelayHandler` takes the credential from the current `HttpContext`: the incoming `Authorization: Bearer` token as is, or the incoming `X-Api-Key` with `X-Tenant-Id` and `X-On-Behalf-Of-User` when present (Claude's testing; a bearer token never brings the tenant or user headers along, and a request with both a token and a key is refused). The credential goes only to `http://<name>-api`: any other host or port in the request URI throws `InvalidOperationException`. The handler sits outside the defaults (service discovery) so it checks the address as written. It always adds `X-Calling-Service` (this service) and `X-Correlation-Id`, and replaces anything the request carried before.
- No service token is ever used: the callee's policies and resource rules judge the user. Without a current request or a credential (a background job, a message handler) it throws `UserCredentialMissingException` (BuildingBlocks.Application) and sends nothing. Callers such as tools turn that into an error result; work with no user keeps using `AddProcureAiServiceClient`.
- In integration tests, point the client at a stub through the stub's `Settings` (`Services:<name>-api:http:0`, Aspire service discovery's configuration source): the relay still checks the address as written (`http://<name>-api`), and service discovery then resolves it to the stub.
- The token is relayed only inside the request that carries it: the call must be made while the request is still being handled (an `await` in the action or a tool called from it), not from a hosted service or a fire-and-forget task.
- Streaming and file responses (`text/event-stream`, `application/pdf`, any `FileResult` or `IResult`) bypass the buffering and ETag of `ConditionalGetFilter` and are written as produced, but still get `Cache-Control: private, no-cache` (unless the action set one) and `Vary: Cookie`.

## Where the pieces live

| Piece | Location |
|---|---|
| `ServiceHost`, `ServiceOptions`, `ServiceStore` (`Postgres`, `MongoDB`, `None`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Hosting` (references Web, Messaging, Persistence and Persistence.Mongo; only `*.Api` projects may use it) |
| MongoDB: `AddProcureAiMongo`, `TenantCollection<T>`, `ITenantDocument`, `IMongoMigration`, `MongoReadinessCheck` (see "A service on MongoDB") | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence.Mongo` |
| A second database on MongoDB next to Postgres: `ServiceSpec.SecondaryMongoDatabase`, `ServiceOptions.SecondaryMongoConnectionName`, `ServiceInfraFixture.UsesMongo` (see "A service with Postgres and a second MongoDB database") | AppHost `ServiceResources.cs`, `BuildingBlocks.Hosting`, `tests/ProcureAi.Testing` |
| `ExcludeFromAudit()` (no audit rows for a table whose class cannot carry `[AuditExclude]`) | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence/AuditExcludeAttribute.cs` |
| The Assistant's AI providers and pipelines (`AiSetup`, `AiOptions`, the Fake provider) | `src/Services/Assistant/ProcureAi.Assistant.Infrastructure/Ai`, `.Application/Ai` |
| `ApplicationExceptionFilter` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Web/Errors` |
| OpenAPI build step | `src/Services/Directory.Build.targets` |
| `AddProcureAiService`, `ServiceSpec`, `SharedResources` | `src/AppHost/ProcureAi.AppHost/ServiceResources.cs` |
| `EnableCdc` / `DisableCdc` | `src/BuildingBlocks/ProcureAi.BuildingBlocks.Persistence/CdcMigrations.cs` |
| `AddDebeziumFor`, Debezium shared properties | `ServiceResources.cs`, `infra/debezium/application.properties` |
| `AddDebeziumForMongo`, MongoDB Debezium properties, `AuditStamp` | `ServiceResources.cs`, `infra/debezium/application-mongo.properties`, `BuildingBlocks.Persistence.Mongo/AuditStamp.cs` |
| Test fixtures, auth and factory | `tests/ProcureAi.Testing` (`ServiceInfraFixture` is the per-service infrastructure base) |
| Concurrency retries of message handlers | `ConcurrencyRetryOptions` (BuildingBlocks.Application, bound from the service's section) and `RetryConcurrencyConflicts` (BuildingBlocks.Messaging) |
| Retries of message handlers when the database fails for a passing reason (transient `NpgsqlException`, `TimeoutException`, also wrapped) | `TransientDatabaseRetryPolicy` (BuildingBlocks.Messaging), set for every service by `UseProcureAiHandlers`: scheduled retries from `Messaging:TransientDatabaseRetry` (defaults 1 s, 5 s, 25 s, 2 min), then dead letters; it leaves lost races and failed service calls to their own rules |
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
