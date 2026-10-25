using System.Reflection;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace ProcureAi.BuildingBlocks.Messaging;

public static class MessagingSetup
{
    public const string RabbitMqConnectionName = "rabbitmq";

    /// <summary>
    /// Wolverine setup for a service whose data is in Postgres through EF Core: handler discovery, the Postgres
    /// outbox/inbox in the service's database, EF Core transactions around handlers, durable inbox and outbox, and
    /// RabbitMQ with caller propagation (<see cref="UseProcureAiTransport"/>). The service adds its own
    /// <c>PublishMessage</c> routes afterwards.
    /// <para>
    /// <c>Wolverine:AutoBuildMessageStorageOnStartup</c> controls the message store tables: on in Development and tests;
    /// elsewhere the service's <c>--migrate</c> step creates them, so startup never changes the schema.
    /// </para>
    /// </summary>
    /// <typeparam name="TAbstraction">The interface handlers take for data access (Wolverine is told it is the DbContext, so its transaction still wraps them).</typeparam>
    /// <typeparam name="TDbContext">The service's DbContext.</typeparam>
    public static WolverineOptions UseProcureAiMessaging<TAbstraction, TDbContext>(
        this WolverineOptions options, IConfiguration configuration, string databaseConnectionName, Assembly handlers)
        where TDbContext : DbContext, TAbstraction
    {
        options.Discovery.IncludeAssembly(handlers);
        options.PersistMessagesWithPostgresql(Required(configuration, databaseConnectionName));
        options.AutoBuildMessageStorageOnStartup =
            configuration.GetValue("Wolverine:AutoBuildMessageStorageOnStartup", AutoCreate.None);
        options.UseEntityFrameworkCoreTransactions().WithDbContextAbstraction<TAbstraction, TDbContext>();
        options.Policies.AutoApplyTransactions();
        options.Policies.UseDurableInboxOnAllListeners();
        options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        return options.UseProcureAiTransport(configuration);
    }

    /// <summary>
    /// RabbitMQ (exchanges and queues created on startup) and caller propagation, for services whose persistence is
    /// not EF Core (the Audit service uses Marten and brings its own outbox).
    /// <para>
    /// Security note: caller headers (<see cref="CallerHeaders"/>) are trusted as they arrive. That is only safe because
    /// the broker is internal and only our own services publish to it. Never expose the broker to untrusted publishers.
    /// </para>
    /// </summary>
    public static WolverineOptions UseProcureAiTransport(this WolverineOptions options, IConfiguration configuration)
    {
        options.UseRabbitMq(new Uri(Required(configuration, RabbitMqConnectionName))).AutoProvision();
        options.AddCallerPropagation();
        return options;
    }

    private static string Required(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException($"Connection string '{name}' is not configured.");
}
