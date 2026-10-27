using System.Reflection;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.Contracts;
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
    /// <para>
    /// Every service except Access also listens to <c>AccessChanged</c> on its own queue (<c>&lt;serviceName&gt;-access-changed</c>,
    /// bound to the <c>access</c> exchange) and evicts the cached Access answers of that tenant (<see cref="AccessChangedHandler"/>).
    /// </para>
    /// </summary>
    /// <param name="serviceName">The service name (e.g. <c>requests-api</c>); names its queues.</param>
    /// <typeparam name="TAbstraction">The interface handlers take for data access (Wolverine is told it is the DbContext, so its transaction still wraps them).</typeparam>
    /// <typeparam name="TDbContext">The service's DbContext.</typeparam>
    public static WolverineOptions UseProcureAiMessaging<TAbstraction, TDbContext>(
        this WolverineOptions options, IConfiguration configuration, string serviceName, string databaseConnectionName, Assembly handlers)
        where TDbContext : DbContext, TAbstraction
    {
        options.PersistMessagesWithPostgresql(Required(configuration, databaseConnectionName));
        options.AutoBuildMessageStorageOnStartup =
            configuration.GetValue("Wolverine:AutoBuildMessageStorageOnStartup", AutoCreate.None);
        options.UseEntityFrameworkCoreTransactions().WithDbContextAbstraction<TAbstraction, TDbContext>();
        return options.UseProcureAiHandlers(configuration, serviceName, handlers);
    }

    /// <summary>
    /// What every service's Wolverine setup shares, whatever stores its messages: handler discovery (the service's and the
    /// shared ones: cache eviction after commit, <c>AccessChanged</c>), transactions around handlers, durable inbox and outbox,
    /// RabbitMQ with caller propagation (<see cref="UseProcureAiTransport"/>), and the queue <c>&lt;serviceName&gt;-access-changed</c>.
    /// <see cref="UseProcureAiMessaging{TAbstraction,TDbContext}"/> calls it for EF Core services; a service with another message
    /// store (Audit: Marten's <c>IntegrateWithWolverine</c>) calls it directly.
    /// </summary>
    /// <param name="listenToAccessChanges">
    /// False for a service whose own queue is already bound to the <c>access</c> exchange (Audit records every event), so
    /// <c>AccessChanged</c> does not arrive twice.
    /// </param>
    public static WolverineOptions UseProcureAiHandlers(
        this WolverineOptions options, IConfiguration configuration, string serviceName, Assembly handlers, bool listenToAccessChanges = true)
    {
        options.Discovery.IncludeAssembly(handlers);
        // The shared handlers (cache eviction after commit, AccessChanged).
        options.Discovery.IncludeAssembly(typeof(MessagingSetup).Assembly);
        options.Policies.AutoApplyTransactions();
        options.Policies.UseDurableInboxOnAllListeners();
        options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        options.UseProcureAiTransport(configuration);
        // The Access client is a typed HttpClient (a factory registration Wolverine cannot inline): handlers that take it
        // (the approval saga) resolve it from the container.
        options.CodeGeneration.AlwaysUseServiceLocationFor<IAccessDirectory>();
        if (listenToAccessChanges)
        {
            ListenToAccessChanges(options, serviceName);
        }

        return options;
    }

    /// <summary>The queue of <paramref name="serviceName"/> that receives <c>AccessChanged</c>.</summary>
    public static string AccessChangedQueue(string serviceName) => $"{serviceName}-access-changed";

    private static void ListenToAccessChanges(WolverineOptions options, string serviceName)
    {
        if (serviceName == AccessClientSetup.AccessServiceName)
        {
            // Access publishes the event and caches nothing from itself: no queue, and no local handler to take it in-process.
            options.Discovery.CustomizeHandlerDiscovery(
                handlers => handlers.Excludes.WithCondition("Access does not listen to its own events", t => t == typeof(AccessChangedHandler)));
            return;
        }

        options.ListenToRabbitQueue(AccessChangedQueue(serviceName), queue => queue.BindExchange(Exchanges.Access));
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
