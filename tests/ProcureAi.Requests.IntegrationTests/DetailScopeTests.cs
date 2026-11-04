using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Requests.Application.Dtos;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>
/// Plan 07 Task 5c: <c>GET {id}</c> and <c>GET by-number/{number}</c> are limited to the same caller scope as the list (own requests,
/// the caller's cost centers incl. managed and headed ones, the whole tenant for the tenant-admin permission, everything for the admin
/// key). Out of scope is 404, never 403, so the response does not tell that the request exists.
/// </summary>
[Collection(InfraCollection.Name)]
public sealed class DetailScopeTests(InfraFixture infra) : IAsyncLifetime
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

    /// <summary>A tenant where Tom (IT-200) raised a request; Alice works for CC-100, Mia manages IT-200, Ada is the tenant admin.</summary>
    private async Task<(string Tenant, RequestDto Request)> TomsRequestAsync(string name)
    {
        var tenant = NewTenant(name);
        infra.Access.SetCostCenters(tenant, "alice", "CC-100");
        infra.Access.SetCostCenters(tenant, "tom", "IT-200");
        infra.Access.SetCostCenters(tenant, "mia", "IT-200");
        infra.Access.SetCostCenters(tenant, "ada");
        infra.Access.SetCostCenters(tenant, "fay");

        using var request = new HttpRequestMessage(HttpMethod.Post, Requests)
        {
            Content = JsonContent.Create(
                new { title = "Tom's", costCenterCode = "IT-200", supplierId = "contoso-hardware", lines = new[] { new { sku = "LAP-1", quantity = 1 } } },
                options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("tom", tenant, "requester"));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await _client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (tenant, (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!);
    }

    private static string Token(string user, string tenant, string role) => TestAuth.CreateToken(user, tenant, role);

    private async Task<HttpResponseMessage> GetAsync(string url, string? bearer, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else
        {
            request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
            request.Headers.Add("X-Tenant-Id", tenant!);
        }

        return await _client.SendAsync(request, Ct);
    }

    private static string[] Urls(RequestDto request) => [$"{Requests}/{request.Id}", $"{Requests}/by-number/{request.Number}"];

    [Fact]
    public async Task Another_cost_centers_request_is_404_by_id_and_by_number()
    {
        var (tenant, toms) = await TomsRequestAsync("idor");

        foreach (var url in Urls(toms))
        {
            using var response = await GetAsync(url, Token("alice", tenant, "requester"));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{url}: Alice acts for CC-100 only");
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Tom's");
        }
    }

    [Fact]
    public async Task Own_cost_center_manager_tenant_admin_and_admin_key_read_it()
    {
        var (tenant, toms) = await TomsRequestAsync("inscope");
        var readers = new (string Who, string? Bearer)[]
        {
            ("the requester", Token("tom", tenant, "requester")),
            ("the manager of IT-200", Token("mia", tenant, "manager")),
            ("the tenant admin (admin.authorization, no cost centers)", Token("ada", tenant, "admin")),
            ("the admin key", null),
        };

        foreach (var url in Urls(toms))
        {
            foreach (var (who, bearer) in readers)
            {
                using var response = await GetAsync(url, bearer, tenant);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url} as {who}: {await response.Content.ReadAsStringAsync(Ct)}");
                (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.Id.ShouldBe(toms.Id);
            }
        }
    }

    [Fact]
    public async Task Finance_approver_without_the_tenant_admin_permission_is_scoped_by_cost_centers()
    {
        // Fay holds the finance role but Access (here) assigns her no cost center: the role alone does not widen her scope.
        var (tenant, toms) = await TomsRequestAsync("finance");

        foreach (var url in Urls(toms))
        {
            using var response = await GetAsync(url, Token("fay", tenant, "finance"));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
        }
    }

    [Fact]
    public async Task One_callers_cached_read_is_never_served_to_an_out_of_scope_caller()
    {
        var (tenant, toms) = await TomsRequestAsync("cache");

        foreach (var url in Urls(toms))
        {
            // Tom's 200 is cached for a second; Alice asks for the same URL inside that second and must not get it.
            using var tomRead = await GetAsync(url, Token("tom", tenant, "requester"));
            using var aliceRead = await GetAsync(url, Token("alice", tenant, "requester"));
            // And the admin key's cached 200 (same URL, same tenant) is not hers either.
            using var keyRead = await GetAsync(url, null, tenant);
            using var aliceAgain = await GetAsync(url, Token("alice", tenant, "requester"));

            tomRead.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            keyRead.StatusCode.ShouldBe(HttpStatusCode.OK, url);
            aliceRead.StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
            aliceAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
            (await aliceRead.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Tom's");
        }
    }

    [Fact]
    public async Task Concurrent_reads_by_an_in_scope_and_an_out_of_scope_caller_are_not_coalesced()
    {
        var (tenant, toms) = await TomsRequestAsync("coalesce");
        var url = $"{Requests}/{toms.Id}";

        // Fired together: single-flight joins identical requests only, and the key carries the user.
        var reads = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            GetAsync(url, Token(i % 2 == 0 ? "tom" : "alice", tenant, "requester"))));

        for (var i = 0; i < reads.Length; i++)
        {
            reads[i].StatusCode.ShouldBe(i % 2 == 0 ? HttpStatusCode.OK : HttpStatusCode.NotFound, $"read {i}");
            reads[i].Dispose();
        }
    }
}
