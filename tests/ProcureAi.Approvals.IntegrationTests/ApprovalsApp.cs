using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Approvals.Application.Sagas;
using ProcureAi.Approvals.Domain;
using ProcureAi.Approvals.Infrastructure;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Budgets;
using ProcureAi.Contracts.Requests;
using Wolverine;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The infrastructure (Postgres, RabbitMQ, Redis, the migrated database), the stub Access service and one running Approvals API
/// for the whole collection, plus a recorder of what Approvals sends: its events (exchange <c>approvals</c>) and its commands to
/// Budgets (queue <c>budgets-commands</c>; the test plays Budgets). Tests use a tenant of their own. The API runs with the
/// production timeouts (a day and more), so nothing times out during a test; timeout tests start a fast instance.
/// </summary>
public sealed class ApprovalsApp : IAsyncLifetime
{
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Timeouts of seconds for the tests of reminders and escalation.</summary>
    public static readonly IReadOnlyDictionary<string, string?> FastTimeouts = new Dictionary<string, string?>
    {
        ["Approvals:Timeouts:ReminderAfter"] = "00:00:02",
        ["Approvals:Timeouts:EscalateAfter"] = "00:00:05",
        ["Approvals:Timeouts:FinanceReminderEvery"] = "00:00:02",
    };

    private readonly ApprovalsInfraFixture _infra = new();

    public ApprovalsInfraFixture Infra => _infra;

    public StubAccessServer Access { get; private set; } = null!;

    public ApprovalsInstance Main { get; private set; } = null!;

    public RabbitRecorder Recorder { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        Main = ApprovalsInstance.Start(_infra.DbConnectionString, _infra.RabbitConnectionString, _infra.RedisConnectionString, Access);
        Recorder = await RabbitRecorder.StartAsync(_infra.RabbitConnectionString, [Exchanges.Approvals], [Queues.BudgetCommands]);
    }

    public async ValueTask DisposeAsync()
    {
        await Recorder.DisposeAsync();
        await Main.DisposeAsync();
        await Access.DisposeAsync();
        await _infra.DisposeAsync();
    }

    /// <summary>
    /// A second Approvals instance with timeouts of seconds and a database of its own (its sagas and scheduled timeouts are its
    /// own). It shares the broker, so a test drives it in-process (<see cref="ApprovalsInstance.InvokeAsync"/>), never through
    /// the Approvals queues, which the main instance also listens to.
    /// </summary>
    /// <summary>
    /// Timeouts hours away, so nothing times out on its own: a test drives the reminder and the escalation by hand, in the order
    /// it wants. Finance reminders (which only follow an overdue alert) come every 2 seconds.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ManualTimeouts = new Dictionary<string, string?>
    {
        ["Approvals:Timeouts:ReminderAfter"] = "01:00:00",
        ["Approvals:Timeouts:EscalateAfter"] = "02:00:00",
        ["Approvals:Timeouts:FinanceReminderEvery"] = "00:00:02",
    };

    public Task<ApprovalsInstance> StartFastInstanceAsync() => StartFastInstanceAsync(FastTimeouts);

    /// <summary>
    /// <see cref="ManualTimeouts"/>, and GET responses cached for 30 seconds instead of 1: a read is only fresh after a change when
    /// the change evicted it (for the cache eviction tests).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ManualTimeoutsLongCache =
        new Dictionary<string, string?>(ManualTimeouts) { ["Caching:DefaultGetSeconds"] = "30" };

    /// <summary>A second instance of the API on the main database and broker (two instances of the service, as in production).</summary>
    public ApprovalsInstance StartSecondMainInstance() =>
        ApprovalsInstance.Start(_infra.DbConnectionString, _infra.RabbitConnectionString, _infra.RedisConnectionString, Access);

    /// <summary>An instance with <see cref="ManualTimeouts"/> and a database of its own.</summary>
    public Task<ApprovalsInstance> StartManualInstanceAsync() => StartFastInstanceAsync(ManualTimeouts);

    /// <summary>As <see cref="StartFastInstanceAsync()"/>, with timeouts of the test's choosing.</summary>
    public async Task<ApprovalsInstance> StartFastInstanceAsync(IReadOnlyDictionary<string, string?> timeouts)
    {
        var database = await _infra.CreateMigratedDatabaseAsync($"fast_{Guid.NewGuid():N}");
        return ApprovalsInstance.Start(database, _infra.RabbitConnectionString, _infra.RedisConnectionString, Access, timeouts);
    }

    public static string NewTenant() => $"t{Guid.NewGuid():N}"[..12];

    /// <summary>
    /// The Acme organisation of cost center IT-200 for <paramref name="tenant"/>: maria manager (R 200 000), dept head of
    /// department (R 500 000), thandi finance (R 2 000 000); everyone may approve.
    /// </summary>
    public void UseAcme(string tenant, bool deptCanApprove = true, bool withFinance = true) =>
        Access.SetApprovers(tenant, new CostCenterApprovers(
            CostCenter, "IT",
            new Approver(DemoUsers.Maria, "Maria", 200_000m, true),
            new Approver(DemoUsers.Dept, "Dept", 500_000m, deptCanApprove),
            withFinance ? [new Approver(DemoUsers.Thandi, "Thandi", 2_000_000m, true)] : []));

    public const string CostCenter = "IT-200";

    public static RequestSubmitted Submitted(string tenant, string requester, decimal total) =>
        new(Guid.NewGuid(), tenant, $"PR-{Random.Shared.Next(1000, 9999)}", "Laptops", requester, CostCenter, total, DateTimeOffset.UtcNow, "SUP-1", []);

    public static BudgetReserved Reserved(RequestSubmitted submitted) =>
        new(submitted.RequestId, submitted.TenantId, submitted.CostCenterCode, "FY2027", submitted.TotalInclVat, DateTimeOffset.UtcNow);

    public static RequestCancelled Cancelled(RequestSubmitted submitted) =>
        new(submitted.RequestId, submitted.TenantId, submitted.Number, submitted.RequesterId, "No longer needed", DateTimeOffset.UtcNow);
}

/// <summary>One running Approvals API (its Program on a test server) and helpers to drive it and read its database.</summary>
public sealed class ApprovalsInstance : IAsyncDisposable
{
    private readonly ApprovalsApiFactory _factory;

    private ApprovalsInstance(ApprovalsApiFactory factory, string database)
    {
        _factory = factory;
        DbConnectionString = database;
    }

    public string DbConnectionString { get; }

    public IMessageBus Bus => _factory.Services.GetRequiredService<IMessageBus>();

    public IServiceProvider Services => _factory.Services;

    public StepReadFault StepReadFault => _factory.StepReadFault;

    /// <summary>An HTTP client of this instance's API (the test server).</summary>
    public HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>
    /// Makes the instance forget its cached Access answers for the tenant, as an <c>AccessChanged</c> does (the next lookup goes to
    /// Access, with no last good answer to fall back on).
    /// </summary>
    public ValueTask ForgetAccessAsync(string tenant) =>
        Services.GetRequiredService<ProcureAi.BuildingBlocks.Web.Access.AccessCacheGeneration>().AdvanceAsync(tenant);

    /// <summary>Sends a message to the instance's own queue (durable local queue), so its error policies apply, as for a timeout.</summary>
    public Task SendAsync(object message, string tenant, string user) => Bus.SendAsync(message, Headers(tenant, user)).AsTask();

    public static ApprovalsInstance Start(
        string database, string rabbit, string redis, StubAccessServer access, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var factory = new ApprovalsApiFactory(database, rabbit, redis, access, settings);
        // Starts the host: Wolverine declares the exchanges and queues and starts listening.
        factory.CreateClient().Dispose();
        return new ApprovalsInstance(factory, database);
    }

    /// <summary>Publishes to an exchange through the broker, as another service would, with the tenant and user in the headers.</summary>
    public Task PublishAsync(object message, string exchange, string tenant, string user = DemoUsers.Tom) =>
        Bus.EndpointFor(new Uri($"rabbitmq://exchange/{exchange}")).SendAsync(message, Headers(tenant, user)).AsTask();

    /// <summary>Handles a message in-process as <paramref name="user"/> of <paramref name="tenant"/> (as the API does for a command); throws what the handler throws.</summary>
    public Task InvokeAsync(object message, string tenant, string user) => Bus.InvokeAsync(message, Headers(tenant, user));

    private static DeliveryOptions Headers(string tenant, string user) =>
        new DeliveryOptions().WithHeader(CallerHeaders.TenantId, tenant).WithHeader(CallerHeaders.UserId, user);

    public ApprovalsDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<ApprovalsDbContext>().UseNpgsql(DbConnectionString).Options);

    /// <summary>The steps of a request, ordered by step number and then by creation (an escalated step before its replacement).</summary>
    public async Task<List<ApprovalStep>> StepsAsync(Guid requestId)
    {
        await using var db = NewDbContext();
        var steps = await db.Steps.IgnoreQueryFilters().AsNoTracking().Where(s => s.RequestId == requestId).ToListAsync();
        return [.. steps.OrderBy(s => s.StepNo).ThenBy(s => s.Id)];
    }

    public async Task<ApprovalSaga?> SagaAsync(Guid requestId)
    {
        await using var db = NewDbContext();
        return await db.Sagas.AsNoTracking().SingleOrDefaultAsync(s => s.Id == requestId);
    }

    /// <summary>Waits until the saga's stored state satisfies <paramref name="done"/> (null: no saga).</summary>
    public async Task<ApprovalSaga?> WaitForSagaAsync(Guid requestId, Func<ApprovalSaga?, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var saga = await SagaAsync(requestId);
            if (done(saga))
            {
                return saga;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Saga {requestId} did not reach the expected state within {timeout} (state: {saga?.State.ToString() ?? "none"}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Messages committed to Wolverine's outbox and not yet delivered.</summary>
    public async Task<long> OutgoingAsync()
    {
        await using var connection = new NpgsqlConnection(DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM wolverine.wolverine_outgoing_envelopes", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class ApprovalsCollection : ICollectionFixture<ApprovalsApp>
{
    public const string Name = "approvals-infra";
}
