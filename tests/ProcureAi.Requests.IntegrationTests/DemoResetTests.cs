using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Testing;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// The nightly demo reset of Requests (plan 06 rulings 4 and 5): <c>POST api/v1/requests/internal/demo/reset</c> deletes every request of
/// every tenant with ordinary deletes the audit interceptor records (one Delete row per deleted row, in the same transaction), restarts
/// the numbering, clears the stored Idempotency-Key results, and is only there when <c>DemoReset:Enabled</c> is true and only for the admin
/// key or a service account. Each test has a database of its own: the reset deletes every tenant's data.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class DemoResetTests(InfraFixture infra)
{
    private const string Requests = "/api/v1/requests";
    private const string Reset = "/api/v1/requests/internal/demo/reset";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_reset_clears_business_rows_and_reseeds()
    {
        var (factory, database) = await StartAsync(enabled: true);
        await using var _ = factory;
        using var client = factory.CreateClient();
        var created = new[]
        {
            await CreateAsync(client, "acme", "Laptops"), await CreateAsync(client, "acme", "Monitors"), await CreateAsync(client, "globex", "Toner"),
        };
        created.Select(r => r.Number).ShouldBe(["PR-0001", "PR-0002", "PR-0001"]);

        (await client.SendAsync(Post(Reset), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ListAsync(client, "acme")).ShouldBeEmpty();
        (await ListAsync(client, "globex")).ShouldBeEmpty();
        // Ordinary deletes: one interceptor row per deleted request and line, all in the reset's one transaction (plan 06 ruling 5).
        var deletes = await DeleteEntriesAsync(database);
        deletes.Where(d => d.Table == "purchase_requests").Select(d => d.Key).ShouldBe(created.Select(r => r.Id.ToString()), ignoreOrder: true);
        deletes.Count(d => d.Table == "request_lines").ShouldBe(created.Length);
        deletes.Select(d => d.TransactionId).Distinct().ShouldHaveSingleItem();
        // Numbering starts again, so a seed after a reset gives the same numbers.
        (await CreateAsync(client, "acme", "Laptops")).Number.ShouldBe("PR-0001");

        // Twice in a row is safe.
        (await client.SendAsync(Post(Reset), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.SendAsync(Post(Reset), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(client, "acme")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Demo_reset_clears_stored_idempotency_results()
    {
        // ProcureAi.DemoData uses fixed Idempotency-Keys (demo:<tenant>:<key>:<step>). Without clearing the stored results, a seed after the
        // reset would be answered with the old 201 of a request that no longer exists.
        var (factory, _) = await StartAsync(enabled: true);
        await using var _ = factory;
        using var client = factory.CreateClient();
        const string key = "demo:acme:laptops:create";
        var before = await CreateAsync(client, "acme", "Laptops", key);

        (await client.SendAsync(Post(Reset), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await CreateAsync(client, "acme", "Laptops", key);
        after.Id.ShouldNotBe(before.Id, "the create ran again instead of replaying the stored answer");
        (await client.SendAsync(Get($"{Requests}/{after.Id}", "acme"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(client, "acme")).Select(r => r.Id).ShouldBe([after.Id]);
    }

    [Fact]
    public async Task Demo_reset_disabled_is_404()
    {
        var (factory, _) = await StartAsync(enabled: false);
        await using var _ = factory;
        using var client = factory.CreateClient();
        var request = await CreateAsync(client, "acme", "Laptops");

        (await client.SendAsync(Post(Reset), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(Post(Reset, bearer: TestAuth.CreateServiceToken(ServiceClientId)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ListAsync(client, "acme")).Select(r => r.Id).ShouldBe([request.Id]);
    }

    [Fact]
    public async Task Demo_reset_refuses_user_tokens_even_an_admins()
    {
        var (factory, _) = await StartAsync(enabled: true);
        await using var _ = factory;
        using var client = factory.CreateClient();
        var request = await CreateAsync(client, "acme", "Laptops");

        var admin = TestAuth.CreateToken("admin-user", "acme", "admin");
        (await client.SendAsync(Post(Reset, bearer: admin), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, Reset);
        (await client.SendAsync(anonymous, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(client, "acme")).Select(r => r.Id).ShouldBe([request.Id]);

        // A service account (the internal client) may: InternalPolicy.
        (await client.SendAsync(Post(Reset, bearer: TestAuth.CreateServiceToken(ServiceClientId)), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(client, "acme")).ShouldBeEmpty();
    }

    private const string ServiceClientId = "procure-internal-test";

    private async Task<(ApiFactory Factory, string Database)> StartAsync(bool enabled)
    {
        var database = await infra.CreateDatabaseAsync($"reset_{Guid.NewGuid():N}");
        var factory = new ApiFactory(infra, settings: new Dictionary<string, string?>
        {
            ["ConnectionStrings:requestsdb"] = database,
            ["DemoReset:Enabled"] = enabled ? "true" : "false",
            ["Auth:InternalClientId"] = ServiceClientId,
        });
        return (factory, database);
    }

    private static async Task<RequestDto> CreateAsync(HttpClient client, string tenant, string title, string? idempotencyKey = null)
    {
        using var request = Get(Requests, tenant);
        request.Method = HttpMethod.Post;
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(
            new { title, costCenterCode = "CC-100", supplierId = "contoso-hardware", lines = new[] { new { sku = "LAP-1", quantity = 1 } } },
            options: Json);
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    private static async Task<IReadOnlyList<RequestDto>> ListAsync(HttpClient client, string tenant)
    {
        // Past the 1 s micro-cache of reads, so the answer is the database's.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        using var response = await client.SendAsync(Get($"{Requests}?pageSize=100", tenant), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("items").Deserialize<List<RequestDto>>(Json)!;
    }

    private static async Task<IReadOnlyList<(string Table, string Key, long TransactionId)>> DeleteEntriesAsync(string database)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select table_name, primary_key, transaction_id from audit_entries where operation = 'Delete'", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string, long)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return rows;
    }

    private static HttpRequestMessage Get(string url, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        return request;
    }

    private static HttpRequestMessage Post(string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (bearer is null)
        {
            request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }
}
