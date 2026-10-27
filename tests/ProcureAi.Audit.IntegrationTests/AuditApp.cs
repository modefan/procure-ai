using System.Net.Http.Headers;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using Wolverine;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>The shared service infrastructure with the audit database migrated by the real <c>--migrate</c> step (Marten schema, Wolverine message store).</summary>
public sealed class AuditInfraFixture() : ServiceInfraFixture("auditdb")
{
    public const string ApiAssembly = "ProcureAi.Audit.Api.dll";

    protected override Task MigrateAsync(string connectionString) =>
        ApiMigrateProcess.RunAsync(ApiAssembly, "auditdb", connectionString, RabbitConnectionString, RedisConnectionString, demoSeed: false);
}

/// <summary>The real Audit API (its Program) on a test server, with the stub Access service.</summary>
public sealed class AuditApiFactory(AuditInfraFixture infra, StubAccessServer access)
    : ServiceApiFactory<Program>("auditdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings)
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>
/// The infrastructure and one running Audit API for the whole collection (listening to <c>audit-events</c>), helpers to publish
/// events to the exchanges as the other services do, and to read what Audit stored. Tests use a tenant of their own.
/// </summary>
public sealed class AuditApp : IAsyncLifetime
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly AuditInfraFixture _infra = new();
    private AuditApiFactory _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public StubAccessServer Access { get; private set; } = null!;

    public AuditInfraFixture Infra => _infra;

    public RabbitProbe Probe => new(_infra.RabbitConnectionString);

    private IMessageBus Bus => _factory.Services.GetRequiredService<IMessageBus>();

    public async ValueTask InitializeAsync()
    {
        await _infra.InitializeAsync();
        Access = await StubAccessServer.StartAsync();
        _factory = new AuditApiFactory(_infra, Access);
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Access.DisposeAsync();
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

    /// <summary>A GET with the admin API key: all permissions without asking Access.</summary>
    public static HttpRequestMessage GetWithApiKey(string url, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        return request;
    }
}

[CollectionDefinition(Name)]
public sealed class AuditCollection : ICollectionFixture<AuditApp>
{
    public const string Name = "audit-infra";
}
