using System.Net.Http.Headers;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Audit.Domain;
using ProcureAi.Budgets.Infrastructure;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Requests.Infrastructure;
using Serilog.Core;
using Serilog.Events;
using Wolverine;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>The shared service infrastructure with the audit database migrated by the real <c>--migrate</c> step (Marten schema, Wolverine message store).</summary>
public sealed class AuditInfraFixture() : ServiceInfraFixture("auditdb")
{
    public const string ApiAssembly = "ProcureAi.Audit.Api.dll";

    /// <summary>Where the Audit Api's appsettings live (the test output folder also gets the Requests Api's).</summary>
    public const string ContentRoot = "src/Services/Audit/ProcureAi.Audit.Api";

    /// <summary>The test password roles.sql gives <c>audit_log_reader</c> (PostgresFixture). Not a secret.</summary>
    public static readonly string AuditLogReaderTestPassword = PostgresFixture.TestPassword("audit_log_reader");

    /// <summary><c>auditdb</c> as <c>audit_log_reader</c>, the role that reads the server's audit log (<c>ConnectionStrings:auditlog</c>).</summary>
    public string AuditLogConnectionString => new Npgsql.NpgsqlConnectionStringBuilder(DbConnectionString)
    {
        Username = "audit_log_reader",
        Password = AuditLogReaderTestPassword,
    }.ConnectionString;

    /// <summary>Another, freshly migrated Audit database on the same Postgres (for a test that must not touch the shared one).</summary>
    public async Task<string> CreateMigratedDatabaseAsync(string name)
    {
        var connectionString = await CreateEmptyDatabaseAsync(name);
        await MigrateAsync(connectionString);
        return connectionString;
    }

    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync(
            ApiAssembly, "auditdb", connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: false, ContentRoot,
            new Dictionary<string, string> { ["ConnectionStrings__auditlog"] = AuditLogConnectionString });
}

/// <summary>Captures Serilog events in memory (registered in DI, so <c>ReadFrom.Services</c> picks it up): what the service logged.</summary>
public sealed class CapturingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }
}

/// <summary>The real Audit API (its Program) on a test server, with the stub Access service.</summary>
public sealed class AuditApiFactory(
    AuditInfraFixture infra, StubAccessServer access, CapturingSink? sink = null, Action<IServiceCollection>? services = null,
    IReadOnlyDictionary<string, string?>? settings = null)
    : ServiceApiFactory<Program>("auditdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    /// <summary>
    /// A change without an interceptor row becomes "outside the app" this long after it arrived (30 s in appsettings): short, so the
    /// tests do not wait long, and still far longer than the gap between the two rows of one transaction.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(4);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Audit:Matching:Grace", Grace.ToString("c"));
        builder.UseSetting("ConnectionStrings:auditlog", infra.AuditLogConnectionString);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(collection =>
        {
            if (sink is not null)
            {
                collection.AddSingleton<ILogEventSink>(sink);
            }

            services?.Invoke(collection);
        });
    }
}

/// <summary>
/// The infrastructure and one running Audit API for the whole collection (listening to <c>audit-events</c> and <c>audit-cdc</c>),
/// helpers to publish events to the exchanges as the other services do, and to read what Audit stored. Tests use a tenant of
/// their own. <see cref="EnsureCdcAsync"/> adds the Requests and Budgets databases with their Debezium Servers and the Requests API.
/// </summary>
public sealed class AuditApp : IAsyncLifetime
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public const string RequestsDb = "requestsdb";
    public const string BudgetsDb = "budgetsdb";

    /// <summary>The tables the AppHost captures in the two databases (<c>ServiceSpec.CapturedTables</c>).</summary>
    public static readonly string[] RequestsTables = ["purchase_requests", "request_lines", "audit_entries"];

    public static readonly string[] BudgetsTables = ["budgets", "budget_reservations", "budget_spends", "audit_entries"];

    private readonly AuditInfraFixture _infra = new();
    private readonly SemaphoreSlim _cdcLock = new(1, 1);
    private AuditApiFactory _factory = null!;
    private RequestsApiFactory? _requests;

    public HttpClient Client { get; private set; } = null!;

    public StubAccessServer Access { get; private set; } = null!;

    public StubApprovalsServer Approvals { get; private set; } = null!;

    /// <summary>The Suppliers catalog Requests prices its lines from.</summary>
    public StubSuppliersServer Suppliers { get; private set; } = null!;

    /// <summary>What the Audit service logged (every test shares the one host).</summary>
    public CapturingSink Logs { get; } = new();

    public AuditInfraFixture Infra => _infra;

    public RabbitProbe Probe => new(_infra.RabbitConnectionString);

    public IServiceProvider Services => _factory.Services;

    /// <summary>Makes a write of a tenant's Access cache generation fail once (a Redis blip).</summary>
    public FlakyGenerationCache GenerationFaults { get; } = new();

    public IMessageBus Bus => _factory.Services.GetRequiredService<IMessageBus>();

    /// <summary>The real Requests API (after <see cref="EnsureCdcAsync"/>), on the same Postgres, broker and Redis.</summary>
    public HttpClient RequestsClient { get; private set; } = null!;

    public string RequestsDbConnectionString { get; private set; } = "";

    public string BudgetsDbConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        Approvals = await StubApprovalsServer.StartAsync();
        Suppliers = await StubSuppliersServer.StartAsync();
        _factory = new AuditApiFactory(_infra, Access, Logs, GenerationFaults.Install);
        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Once per run: the Requests and Budgets databases, migrated by their own migrations (which create the publications), each
    /// with its own Debezium Server publishing to <c>cdc</c>, and the real Requests API on top. Only the tests that need real CDC
    /// pay for it.
    /// </summary>
    public async Task EnsureCdcAsync()
    {
        await _cdcLock.WaitAsync();
        try
        {
            if (_requests is not null)
            {
                return;
            }

            RequestsDbConnectionString = await _infra.AddCapturedDatabaseAsync(
                RequestsDb, connectionString => RequestsDatabase.MigrateAsync(connectionString), RequestsTables);
            BudgetsDbConnectionString = await _infra.AddCapturedDatabaseAsync(BudgetsDb, MigrateBudgetsAsync, BudgetsTables);
            _requests = new RequestsApiFactory(_infra, RequestsDbConnectionString, Access, Approvals, Suppliers);
            RequestsClient = _requests.CreateClient();
        }
        finally
        {
            _cdcLock.Release();
        }
    }

    /// <summary>The MongoDB collections the AppHost's <c>debezium-mongo</c> captures (plan 06 ruling 16).</summary>
    public static readonly string[] MongoCollections = ["suppliersdb.suppliers", "assistantchatdb.conversations"];

    /// <summary>Once per run: a MongoDB replica set and the MongoDB Debezium Server capturing <see cref="MongoCollections"/>.</summary>
    public Task<MongoFixture> EnsureMongoCdcAsync() => _infra.EnsureMongoCdcAsync(MongoCollections);

    private static async Task MigrateBudgetsAsync(string connectionString)
    {
        await using var db = new BudgetsDbContext(
            Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions.UseNpgsql(
                new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BudgetsDbContext>(), connectionString).Options);
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database);
    }

    /// <summary>A second Audit host on the same infrastructure (e.g. with a fake clock); the caller disposes it.</summary>
    public AuditApiFactory CreateHost(Action<IServiceCollection> services, IReadOnlyDictionary<string, string?>? settings = null) =>
        new(_infra, Access, sink: null, services, settings);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_requests is not null)
        {
            RequestsClient.Dispose();
            await _requests.DisposeAsync();
        }

        await _factory.DisposeAsync();
        await Access.DisposeAsync();
        await Approvals.DisposeAsync();
        await Suppliers.DisposeAsync();
        await _infra.DisposeAsync();
    }

    public static string NewTenant() => $"t{Guid.NewGuid():N}"[..12];

    /// <summary>Publishes an event to an exchange through the broker, with the initiating user and tenant in the headers (as every service does).</summary>
    public Task PublishAsync(object message, string exchange, string tenant, string user) =>
        Bus.EndpointFor(new Uri($"rabbitmq://exchange/{exchange}"))
            .SendAsync(message, new DeliveryOptions().WithHeader(CallerHeaders.TenantId, tenant).WithHeader(CallerHeaders.UserId, user))
            .AsTask();

    /// <summary>Everything stored for a tenant, straight from Marten.</summary>
    public async Task<IReadOnlyList<BusinessEvent>> StoredAsync(string tenant)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.Query<BusinessEvent>().Where(e => e.TenantId == tenant).ToListAsync();
    }

    /// <summary>Waits until <paramref name="done"/> holds for the tenant's stored events; fails with what was stored.</summary>
    public async Task<IReadOnlyList<BusinessEvent>> WaitForStoredAsync(string tenant, Func<IReadOnlyList<BusinessEvent>, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        IReadOnlyList<BusinessEvent> stored = [];
        while (DateTime.UtcNow < deadline)
        {
            stored = await StoredAsync(tenant);
            if (done(stored))
            {
                return stored;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"Audit did not store the expected events of {tenant}. Stored: {string.Join(", ", stored.Select(e => e.Type))}");
    }

    /// <summary>Documents of one type straight from Marten.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(Func<IQueryable<T>, IQueryable<T>> filter)
        where T : notnull
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await filter(session.Query<T>()).ToListAsync();
    }

    /// <summary>Waits until the documents that <paramref name="filter"/> selects satisfy <paramref name="done"/>; fails with what was there.</summary>
    public async Task<IReadOnlyList<T>> WaitForAsync<T>(
        Func<IQueryable<T>, IQueryable<T>> filter, Func<IReadOnlyList<T>, bool> done, TimeSpan? timeout = null)
        where T : notnull
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        IReadOnlyList<T> found = [];
        while (DateTime.UtcNow < deadline)
        {
            found = await QueryAsync(filter);
            if (done(found))
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"The expected {typeof(T).Name} documents did not appear. Found: {string.Join(" | ", found)}");
    }

    /// <summary>The durable inbox's status of a message (null when the inbox has no row for it).</summary>
    public async Task<string?> InboxStatusAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(_infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status FROM wolverine.wolverine_incoming_envelopes WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", messageId);
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>Removes a message from the durable inbox, as its retention would after a while, so only the document ID can stop a redelivery.</summary>
    public async Task ForgetInboxAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(_infra.DbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM wolverine.wolverine_incoming_envelopes WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", messageId);
        await command.ExecuteNonQueryAsync();
    }

    public static HttpRequestMessage Get(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>A GET with the admin API key: all permissions without asking Access. Without a tenant it acts across all tenants.</summary>
    public static HttpRequestMessage GetWithApiKey(string url, string? tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenant);
        }

        return request;
    }
}

[CollectionDefinition(Name)]
public sealed class AuditCollection : ICollectionFixture<AuditApp>
{
    public const string Name = "audit-infra";
}
