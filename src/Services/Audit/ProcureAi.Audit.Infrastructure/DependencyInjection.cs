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
using ProcureAi.Audit.Domain;
using ProcureAi.Audit.Infrastructure.Retention;
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
                    .Index(c => c.Database);
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
        return services;
    }

    /// <summary>
    /// The shared handler setup (durable inbox, caller headers, shared handlers) and two queues:
    /// <list type="bullet">
    /// <item><c>audit-events</c>, bound with <c>#</c> to every exchange in <see cref="AuditedExchanges"/>. Every integration event type
    /// of the contracts is registered up front, so Wolverine knows each one when it arrives (the recording handler takes the
    /// <c>IIntegrationEvent</c> interface). <c>AccessChanged</c> reaches this queue too (exchange <c>access</c>), so the service has no
    /// separate access-changed queue: one delivery both records it and evicts the cached Access answers.</item>
    /// <item><c>audit-cdc</c>, bound with <c>#</c> to the topic exchange <c>cdc</c> only. Debezium publishes plain JSON without
    /// Wolverine's headers, so every message on it is read as a <see cref="DebeziumChange"/>. It is durable like every listener: a
    /// message is acknowledged once it is in the durable inbox (Postgres), and handled from there.</item>
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
        options.ListenToRabbitQueue(CdcQueue, queue => queue.BindExchange(CdcExchange, "#"))
            .DefaultIncomingMessage<DebeziumChange>()
            .Sequential();

        // The grace timeout survives a restart: scheduled in the durable message store.
        options.LocalQueueFor<ResolveAttribution>().UseDurableInbox();

        const string section = $"{AuditOptions.SectionName}:ConcurrencyRetry";
        var retry = configuration.GetSection(section).Get<ProcureAi.BuildingBlocks.Application.ConcurrencyRetryOptions>()
            ?? throw new InvalidOperationException($"'{section}' is not configured.");
        options.RetryConcurrencyConflicts(retry, section);
    }

    private static IReadOnlyCollection<Expression<Func<T, object>>> Columns<T>(params Expression<Func<T, object>>[] columns) => columns;
}
