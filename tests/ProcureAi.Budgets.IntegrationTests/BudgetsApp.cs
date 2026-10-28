using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Contracts;
using RabbitMQ.Client;
using Wolverine;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>
/// The infrastructure (Postgres, RabbitMQ, Redis, the migrated database) and one running Budgets API for the whole collection (its Program on a test server, listening to the real <c>budgets-commands</c>
/// queue), the events it publishes, and helpers to send commands and call the HTTP API. Tests use a tenant of their own.
/// </summary>
public sealed class BudgetsApp : IAsyncLifetime
{
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    private readonly BudgetsInfraFixture _infra = new();
    private StubAccessServer _access = null!;
    private BudgetsApiFactory _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public BudgetEvents Events { get; private set; } = null!;

    public BudgetsInfraFixture Infra => _infra;

    /// <summary>The Access service the API's permission checks ask: a stub with a matrix per tenant (every test uses a tenant of its own).</summary>
    public StubAccessServer Access => _access;

    public IMessageBus Bus => _factory.Services.GetRequiredService<IMessageBus>();

    public async ValueTask InitializeAsync()
    {
        await _infra.InitializeAsync();
        _access = await StubAccessServer.StartAsync();
        _factory = new BudgetsApiFactory(_infra, _access);
        Client = _factory.CreateClient();
        Events = await BudgetEvents.StartAsync(_infra.RabbitConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync();
        Client.Dispose();
        await _factory.DisposeAsync();
        await _access.DisposeAsync();
        await _infra.DisposeAsync();
    }

    /// <summary>Starts more instances of the API on the same infrastructure; they compete for the queue like replicas in a cluster.</summary>
    public async Task<IAsyncDisposable> StartExtraInstancesAsync(int count)
    {
        var extra = Enumerable.Range(0, count).Select(_ => new BudgetsApiFactory(_infra, _access)).ToList();
        foreach (var factory in extra)
        {
            factory.CreateClient().Dispose();
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        return new Instances(extra);
    }

    private sealed class Instances(List<BudgetsApiFactory> factories) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var factory in factories)
            {
                await factory.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Sends a command to the <c>budgets-commands</c> queue through the broker, as the approval flow does, with the tenant and user
    /// of the sender in the headers.
    /// </summary>
    public Task SendAsync(object command, string tenant) =>
        Bus.EndpointFor(new Uri($"rabbitmq://queue/{Queues.BudgetCommands}"))
            .SendAsync(command, new DeliveryOptions().WithHeader("x-tenant-id", tenant).WithHeader("x-user-id", "tom")).AsTask();

    public static HttpRequestMessage Request(HttpMethod method, string url, string token, string? ifMatch = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public static string Token(string tenant, string role, string user = "u1") => TestAuth.CreateToken(user, tenant, role);

    public async Task<BudgetDto> GetBudgetAsync(string tenant, string costCenter, string role = "finance")
    {
        var response = await Client.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/budgets/{costCenter}/{BudgetsInfraFixture.CurrentYear}", Token(tenant, role)),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BudgetDto>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>The stored state of a budget straight from the database (no cache): reserved total, available, and the sum of the unreleased reservations.</summary>
    public async Task<(decimal Reserved, decimal Available, decimal Active)> StoredAsync(string tenant, string costCenter)
    {
        await using var connection = new NpgsqlConnection(_infra.DbConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT b.reserved, b.amount - b.reserved - b.spent,
                   COALESCE((SELECT sum(r.amount - r.spent) FROM budget_reservations r WHERE r.budget_id = b.id AND NOT r.is_released), 0)
            FROM budgets b WHERE b.tenant_id = @t AND b.cost_center_code = @c AND b.fiscal_year = @y
            """,
            connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("c", costCenter);
        command.Parameters.AddWithValue("y", BudgetsInfraFixture.CurrentYear.EndYear);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
    }

    /// <summary>The spent total of a budget and the number of payments recorded on it, straight from the database.</summary>
    public async Task<(decimal Spent, long Payments)> SpentAsync(string tenant, string costCenter)
    {
        await using var connection = new NpgsqlConnection(_infra.DbConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT b.spent, (SELECT count(*) FROM budget_spends s WHERE s.budget_id = b.id)
            FROM budgets b WHERE b.tenant_id = @t AND b.cost_center_code = @c AND b.fiscal_year = @y
            """,
            connection);
        command.Parameters.AddWithValue("t", tenant);
        command.Parameters.AddWithValue("c", costCenter);
        command.Parameters.AddWithValue("y", BudgetsInfraFixture.CurrentYear.EndYear);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return (reader.GetDecimal(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Messages that ran out of retries or were moved to the error queue: the rows of Wolverine's dead-letter table plus the
    /// messages in the broker's <c>wolverine-dead-letter-queue</c> (where the RabbitMQ transport puts a message moved to the error queue).
    /// Tests compare it with its value before.
    /// </summary>
    public Task<long> DeadLettersAsync() => DeadLetters.CountAsync(_infra.DbConnectionString, _infra.RabbitConnectionString);
}

[CollectionDefinition(Name)]
public sealed class BudgetsCollection : ICollectionFixture<BudgetsApp>
{
    public const string Name = "budgets-infra";
}
