using System.Linq.Expressions;
using System.Reflection;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcureAi.Audit.Application;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Cdc;
using ProcureAi.Audit.Application.Events;
using ProcureAi.Audit.Application.Status;
using ProcureAi.BuildingBlocks.Web.Status;
using ProcureAi.Audit.Domain;
using ProcureAi.Audit.Infrastructure.PgAudit;
using ProcureAi.Audit.Infrastructure.Retention;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

namespace ProcureAi.Audit.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseConnectionName = "auditdb";

    /// <summary>The queue every integration event reaches: bound to every exchange of <see cref="Exchanges"/>.</summary>
    public const string EventsQueue = "audit-events";

    /// <summary>The queue Debezium's changes reach: bound to the <c>cdc</c> exchange only (and nothing else is bound to it).</summary>
    public const string CdcQueue = "audit-cdc";

    /// <summary>The queue the MongoDB Debezium Server's changes reach (plan 06 ruling 16): bound to <c>cdc</c> with <see cref="CdcMongoRoutingKey"/>.</summary>
    public const string CdcMongoQueue = "audit-cdc-mongo";

    /// <summary>The Postgres Debezium Servers' routing keys, <c>&lt;database&gt;.public.&lt;table&gt;</c> (and no MongoDB key matches it).</summary>
    public const string CdcPostgresRoutingKey = "*.public.*";

    /// <summary>The MongoDB Debezium Server's routing keys, <c>mongo.&lt;database&gt;.&lt;collection&gt;</c>.</summary>
    public const string CdcMongoRoutingKey = "mongo.#";

    /// <summary>The topic exchange every Debezium Server publishes to (routing key <c>&lt;database&gt;.public.&lt;table&gt;</c>); declared by the AppHost.</summary>
    public const string CdcExchange = "cdc";

    /// <summary>Marten's documents live in this schema of <c>auditdb</c>; Wolverine's inbox and outbox in <c>wolverine</c>.</summary>
    public const string DocumentSchema = "audit";

    public const string MessageStoreSchema = "wolverine";

    /// <summary>
    /// Every exchange services publish integration events to (the public constants of <see cref="Exchanges"/>), so an exchange
    /// added there is audited without a change here.
    /// </summary>
    public static IReadOnlyList<string> AuditedExchanges { get; } = typeof(Exchanges)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    /// <summary>
    /// Marten on <c>auditdb</c> (documents in schema <c>audit</c>), integrated with Wolverine: its message store (durable inbox
    /// and outbox) lives in the same database. Marten's <c>AutoCreateSchemaObjects</c> governs both: <c>Audit:Marten:AutoCreateSchemaObjects</c>
    /// (<c>None</c> except in Development) at normal startup, so startup never changes the schema, and <c>CreateOrUpdate</c> when
    /// <paramref name="migrating"/> (the <c>--migrate</c> run creates the documents' and the message store's tables).
    /// </summary>
    public static IServiceCollection AddAuditInfrastructure(this IServiceCollection services, IConfiguration configuration, bool migrating)
    {
        var connectionString = configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"Connection string {DatabaseConnectionName} is not configured.");
        var autoCreate = migrating
            ? AutoCreate.CreateOrUpdate
            : configuration.GetValue("Audit:Marten:AutoCreateSchemaObjects", AutoCreate.None);
        services.AddMarten(options =>
            {
                options.Connection(connectionString);
                options.DatabaseSchemaName = DocumentSchema;
                options.AutoCreateSchemaObjects = autoCreate;

                // The history of a request is read by tenant and request: one index on both.
                options.Schema.For<BusinessEvent>().Index(Columns<BusinessEvent>(e => e.TenantId, e => e.SubjectId!));

                // A pending change is decided by its match or by the grace timeout, never both: versions (optimistic concurrency).
                options.Schema.For<DataChange>()
                    .UseOptimisticConcurrency(true)
                    .Metadata(metadata => metadata.Version.MapTo(c => c.Version))
                    .Index(Columns<DataChange>(c => c.TenantId!, c => c.SubjectId!))
                    .Index(c => c.CommittedAt)
                    .Index(c => c.Database)
                    // A MongoDB change is decided against the document's previous change (LatestChangeAsync).
                    .Index(Columns<DataChange>(c => c.Database, c => c.Table, c => c.PrimaryKey));
                options.Schema.For<AttributionMatch>()
                    .UseOptimisticConcurrency(true)
                    .Metadata(metadata => metadata.Version.MapTo(m => m.Version))
                    .Index(m => m.LastSeenAt);
                options.Schema.For<InterceptorEntry>().Index(e => e.OccurredAt);
                options.Schema.For<OutsideAppAlert>().Index(a => a.CommittedAt);
            })
            .IntegrateWithWolverine(wolverine => wolverine.MessageStorageSchemaName = MessageStoreSchema);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditStore, MartenAuditStore>();
        services.AddSingleton<IDataChangeStore, MartenDataChangeStore>();
        services.AddSingleton<IAuditMetrics, AuditMetrics>();
        services.AddHostedService<DataChangeRetention>();
        services.AddScoped<ProcureAi.BuildingBlocks.Web.Demo.IDemoResetter, AuditDemoResetter>();
        services.AddStatusContributor<AuditStatusContributor>();
        services.AddPgAuditSummary(configuration);
        return services;
    }

    /// <summary>
    /// The shared handler setup (durable inbox, caller headers, shared handlers) and three queues:
    /// <list type="bullet">
    /// <item><c>audit-events</c>, bound with <c>#</c> to every exchange in <see cref="AuditedExchanges"/>. Every integration event type
    /// of the contracts is registered up front, so Wolverine knows each one when it arrives (the recording handler takes the
    /// <c>IIntegrationEvent</c> interface). <c>AccessChanged</c> reaches this queue too (exchange <c>access</c>), so the service has no
    /// separate access-changed queue: one delivery both records it and evicts the cached Access answers.</item>
    /// <item><c>audit-cdc</c>, bound with <c>*.public.*</c> (the Postgres Debezium Servers' keys) to the topic exchange <c>cdc</c> only.
    /// Debezium publishes plain JSON without Wolverine's headers, so every message on it is read as a <see cref="DebeziumChange"/>. It is
    /// durable like every listener: a message is acknowledged once it is in the durable inbox (Postgres), and handled from there.</item>
    /// <item><c>audit-cdc-mongo</c>, bound with <c>mongo.#</c> to <c>cdc</c>: the MongoDB Debezium Server's changes, read as
    /// <see cref="DebeziumMongoMessage"/> (plan 06 ruling 16).</item>
    /// </list>
    /// A handler that lost a race (concurrency exception) is retried inline with backoff and jitter (<c>Audit:ConcurrencyRetry</c>).
    /// </summary>
    public static void ConfigureAudit(this WolverineOptions options, IConfiguration configuration, string serviceName)
    {
        options.AutoBuildMessageStorageOnStartup =
            configuration.GetValue("Wolverine:AutoBuildMessageStorageOnStartup", AutoCreate.None);
        options.UseProcureAiHandlers(configuration, serviceName, typeof(RecordBusinessEventHandler).Assembly, listenToAccessChanges: false);
        foreach (var type in IntegrationEventTypes.All)
        {
            options.RegisterMessageType(type);
        }

        options.ListenToRabbitQueue(EventsQueue, queue =>
        {
            foreach (var exchange in AuditedExchanges)
            {
                queue.BindExchange(exchange, "#");
            }
        });

        options.ConfigureRabbitMq().DeclareExchange(CdcExchange, exchange =>
        {
            exchange.ExchangeType = Wolverine.RabbitMQ.ExchangeType.Topic;
            exchange.IsDurable = true;
        });
        // One message at a time per instance: Debezium's order is kept, and the change and the interceptor row of one transaction
        // (published back to back) do not race each other on every app write (each lost race is retried, but logged as an error).
        // Several instances still compete for the queue; the matches' optimistic concurrency covers that.
        // The Postgres changes only (<database>.public.<table>): the MongoDB ones have their own queue and their own message shape.
        options.ListenToRabbitQueue(CdcQueue, queue => queue.BindExchange(CdcExchange, CdcPostgresRoutingKey))
            .DefaultIncomingMessage<DebeziumChange>()
            .Sequential();

        // The MongoDB changes (mongo.<database>.<collection>, plan 06 ruling 16), one at a time like the Postgres ones: a document's
        // change is decided against its previous one, so their order matters.
        options.ListenToRabbitQueue(CdcMongoQueue, queue => queue.BindExchange(CdcExchange, CdcMongoRoutingKey))
            .DefaultIncomingMessage<DebeziumMongoMessage>()
            .Sequential();

        // The grace timeout survives a restart: scheduled in the durable message store.
        options.LocalQueueFor<ResolveAttribution>().UseDurableInbox();

        const string section = $"{AuditOptions.SectionName}:ConcurrencyRetry";
        var retry = configuration.GetSection(section).Get<ProcureAi.BuildingBlocks.Application.ConcurrencyRetryOptions>()
            ?? throw new InvalidOperationException($"'{section}' is not configured.");
        options.RetryConcurrencyConflicts(retry, section);
    }

    /// <summary>
    /// The pgaudit summary (plan 06 ruling 14): <see cref="PgAuditOptions"/> (section <c>PgAudit</c>, validated at startup), the reader on
    /// <c>ConnectionStrings:&lt;PgAudit:ConnectionName&gt;</c> (required: the service does not start without it) and the <c>pgaudit</c> status section.
    /// </summary>
    private static void AddPgAuditSummary(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<PgAuditOptions>(configuration, PgAuditOptions.SectionName);
        services.AddOptions<PgAuditOptions>().Validate(
            options => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(options.ConnectionName)),
            "PgAudit: the connection string named by PgAudit:ConnectionName (audit_log_reader) is not configured.");
        services.AddSingleton<IPgAuditReader>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PgAuditOptions>>().Value;
            return new PgAuditReader(AuditLogConnectionString(configuration, options), options, provider.GetRequiredService<TimeProvider>());
        });
        services.AddStatusContributor<PgAuditContributor>();
    }

    /// <summary>The connection string of <c>audit_log_reader</c> (infra/postgres/roles.sql creates the role with this password).</summary>
    public static string AuditLogConnectionString(IConfiguration configuration, PgAuditOptions options) =>
        configuration.GetConnectionString(options.ConnectionName)
        ?? throw new InvalidOperationException($"Connection string {options.ConnectionName} (the audit log reader) is not configured.");

    private static IReadOnlyCollection<Expression<Func<T, object>>> Columns<T>(params Expression<Func<T, object>>[] columns) => columns;
}
