using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Requests.Application.Dtos;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// <c>GET api/v1/requests/by-number/{number}</c> (plan 05 Task 5b, the Assistant's <c>get_request_status</c> tool): the same request as
/// <c>GET {id}</c>, found by its human-readable number, with the same permission and tenant scope.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class ByNumberTests(InfraFixture infra) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string Requests = "/api/v1/requests";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(infra);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewTenant(string name) => $"{name}-{Guid.NewGuid():N}"[..24];

    private async Task<RequestDto> CreateAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Requests)
        {
            Content = JsonContent.Create(
                new { title = "Laptops", costCenterCode = "CC-100", supplierId = "contoso-hardware", lines = new[] { new { sku = "LAP-1", quantity = 3 } } },
                options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await _client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    private async Task<HttpResponseMessage> ByNumberAsync(string number, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Requests}/by-number/{number}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task By_number_returns_request_in_scope()
    {
        var tenant = NewTenant("bynumber");
        var created = await CreateAsync(TestAuth.CreateToken("alice", tenant, "requester"));

        // Another user of the tenant reads it, as with GET {id}; the number is matched with or without its zero padding.
        using var response = await ByNumberAsync(created.Number, TestAuth.CreateToken("bob", tenant, "requester"));
        using var unpadded = await ByNumberAsync("PR-1", TestAuth.CreateToken("bob", tenant, "requester"));
        using var unknown = await ByNumberAsync("PR-0999", TestAuth.CreateToken("bob", tenant, "requester"));
        using var invalid = await ByNumberAsync("PO-0001", TestAuth.CreateToken("bob", tenant, "requester"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var dto = (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        dto.Id.ShouldBe(created.Id);
        dto.Number.ShouldBe("PR-0001");
        dto.Title.ShouldBe("Laptops");
        response.Headers.ETag!.Tag.ShouldBe($"\"{created.Version}\"");
        unpadded.StatusCode.ShouldBe(HttpStatusCode.OK);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        invalid.StatusCode.ShouldBe(HttpStatusCode.NotFound, "not a request number: nothing to find");
    }

    [Fact]
    public async Task By_number_of_other_tenant_is_404()
    {
        var acme = NewTenant("acme");
        var globex = NewTenant("globex");
        var created = await CreateAsync(TestAuth.CreateToken("alice", acme, "requester"));
        // Globex has a request of its own with the same number: its own one is found, never Acme's.
        var own = await CreateAsync(TestAuth.CreateToken("gary", globex, "requester"));

        using var other = await ByNumberAsync(created.Number, TestAuth.CreateToken("gina", NewTenant("initech"), "requester"));
        using var sameNumber = await ByNumberAsync(created.Number, TestAuth.CreateToken("gina", globex, "requester"));
        using var noPermission = await ByNumberAsync(created.Number, TestAuth.CreateToken("nina", acme, "nobody"));

        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        sameNumber.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sameNumber.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.Id.ShouldBe(own.Id);
        noPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
