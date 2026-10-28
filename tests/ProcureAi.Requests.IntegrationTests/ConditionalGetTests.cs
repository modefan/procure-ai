using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProcureAi.Requests.Application.Dtos;
using Shouldly;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>Conditional GETs (ETag / If-None-Match) on the real Requests API: the detail keeps its version tag, the list gets a weak one.</summary>
[Collection(InfraCollection.Name)]
public sealed class ConditionalGetTests(InfraFixture infra) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Requests = "/api/v1/requests";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(infra, CapturingSink.Shared);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string tenant, object? body = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return request;
    }

    private async Task<RequestDto> CreateAsync(string tenant)
    {
        var body = new
        {
            title = "Laptops",
            costCenterCode = "CC-100",
            supplierId = "contoso-hardware",
            lines = new[] { new { sku = "LAP-1", quantity = 3 } },
        };
        using var response = await _client.SendAsync(Req(HttpMethod.Post, Requests, tenant, body), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    [Fact]
    public async Task Detail_keeps_its_version_etag_and_answers_304_to_it()
    {
        var tenant = $"cond-{Guid.NewGuid():N}"[..24];
        var created = await CreateAsync(tenant);
        var tag = $"\"{created.Version}\"";

        using var first = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant), Ct);
        using var again = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant, ifNoneMatch: tag), Ct);

        first.Headers.ETag!.ToString().ShouldBe(tag);
        first.Headers.CacheControl!.NoCache.ShouldBeTrue();
        again.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await again.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_gets_a_weak_etag_that_changes_with_the_content()
    {
        var tenant = $"cond-{Guid.NewGuid():N}"[..24];
        await CreateAsync(tenant);
        using var first = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant), Ct);
        var tag = first.Headers.ETag!.ToString();
        tag.ShouldStartWith("W/\"");

        using var same = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant, ifNoneMatch: tag), Ct);
        same.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        await CreateAsync(tenant);
        // The list is micro-cached for a second; wait it out so the new request shows.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);
        using var changed = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant, ifNoneMatch: tag), Ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        changed.Headers.ETag!.ToString().ShouldNotBe(tag);
    }
}
