using System.Reflection;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Audit.Application;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Events;
using ProcureAi.Audit.Domain;
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
                // The history of a request is read by tenant and request.
                options.Schema.For<BusinessEvent>().Index(e => e.TenantId).Index(e => e.SubjectId!);
            })
            .IntegrateWithWolverine(wolverine => wolverine.MessageStorageSchemaName = MessageStoreSchema);
        services.AddSingleton<IAuditStore, MartenAuditStore>();
        return services;
    }

    /// <summary>
    /// The shared handler setup (durable inbox, caller headers, shared handlers) and the queue <c>audit-events</c>, bound with
    /// <c>#</c> to every exchange in <see cref="AuditedExchanges"/>. Every integration event type of the contracts is registered
    /// up front, so Wolverine knows each one when it arrives (the recording handler takes the <c>IIntegrationEvent</c> interface).
    /// <c>AccessChanged</c> reaches this queue too (exchange <c>access</c>), so the service has no separate access-changed queue:
    /// one delivery both records it and evicts the cached Access answers.
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
    }
}
