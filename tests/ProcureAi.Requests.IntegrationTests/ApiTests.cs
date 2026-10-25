using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Domain;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>The Requests API over HTTP: real Program, Postgres, RabbitMQ and Redis; auth by the admin API key and test JWTs.</summary>
[Collection(InfraCollection.Name)]
public sealed class ApiTests(InfraFixture infra) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Requests = "/api/v1/requests";

    private readonly CapturingSink _sink = new();
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(infra, _sink);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewTenant(string name) => $"{name}-{Guid.NewGuid():N}"[..24];

    private static object NewBody(string title = "Laptops") => new
    {
        title,
        costCenterCode = "CC-100",
        supplierId = "sup-1",
        lines = new[] { new { sku = "LAP-1", description = "Laptop", quantity = 3, unitPriceExclVat = 25200m } },
    };

    private static HttpRequestMessage Req(
        HttpMethod method, string url, object? body = null, string? tenant = null, string? bearer = null,
        string? ifMatch = null, string? idempotencyKey = null, bool apiKey = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        else if (apiKey)
        {
            request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
            if (tenant is not null)
            {
                request.Headers.Add("X-Tenant-Id", tenant);
            }
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private async Task<RequestDto> CreateAsync(string tenant, string? bearer = null, string title = "Laptops")
    {
        var response = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(title), tenant, bearer, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    private static string ETagOf(RequestDto dto) => $"\"{dto.Version}\"";

    [Fact]
    public async Task Create_with_api_key_returns_201_with_etag_and_location()
    {
        var tenant = NewTenant("create");

        var response = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var dto = (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        dto.Status.ShouldBe(RequestStatus.Draft);
        dto.Number.ShouldBe("PR-0001");
        dto.TotalInclVatDisplay.ShouldBe("R 86 940,00");
        response.Headers.ETag!.Tag.ShouldBe($"\"{dto.Version}\"");
        response.Headers.ETag.IsWeak.ShouldBeFalse();
        response.Headers.Location!.AbsolutePath.ShouldBe($"{Requests}/{dto.Id}");
        // The Location works for the same caller.
        var read = await _client.SendAsync(Req(HttpMethod.Get, response.Headers.Location.AbsolutePath, tenant: tenant), Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.Headers.ETag!.Tag.ShouldBe(response.Headers.ETag.Tag);
    }

    [Fact]
    public async Task Ten_parallel_submits_with_same_key_submit_once()
    {
        // Two instances of the API on the same infrastructure; the duplicates are spread over both.
        await using var second = new ApiFactory(infra);
        using var clientB = second.CreateClient();
        var tenant = NewTenant("double");
        var created = await CreateAsync(tenant);
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync("requests");
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var client = i % 2 == 0 ? _client : clientB;
            using var response = await client.SendAsync(
                Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", null, tenant, ifMatch: ETagOf(created), idempotencyKey: key), Ct);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(Ct), ETag: response.Headers.ETag?.Tag);
        }));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted);
        responses.Select(r => r.Body).Distinct().Count().ShouldBe(1);
        responses.Select(r => r.ETag).Distinct().Count().ShouldBe(1);
        var body = JsonSerializer.Deserialize<RequestDto>(responses[0].Body, Json)!;
        body.Status.ShouldBe(RequestStatus.Submitted);
        // One state change: the version moved once. One message: exactly one RequestSubmitted reached the exchange.
        await probe.WaitForCountAsync(queue, 1, TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        (await probe.CountAsync(queue)).ShouldBe(1u);
        var current = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant: tenant), Ct);
        current.Headers.ETag!.Tag.ShouldBe(responses[0].ETag);
    }

    [Fact]
    public async Task Submit_publishes_RequestSubmitted_with_caller_headers()
    {
        var tenant = NewTenant("headers");
        var created = await CreateAsync(tenant);
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync("requests");
        var correlationId = $"corr-{Guid.NewGuid():N}";
        var request = Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", null, tenant, ifMatch: ETagOf(created), idempotencyKey: Guid.NewGuid().ToString());
        request.Headers.Add("X-Correlation-Id", correlationId);

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await probe.WaitForCountAsync(queue, 1, TimeSpan.FromSeconds(30));
        var headers = await probe.TakeHeadersAsync(queue);
        headers["x-tenant-id"].ShouldBe(tenant);
        headers["x-user-id"].ShouldBe("claude-admin");
        headers["x-correlation-id"].ShouldBe(correlationId);
        headers.Keys.ShouldNotContain(k => k.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Consuming_handler_gets_caller_and_log_scope_from_message_headers()
    {
        var marker = Guid.NewGuid();
        var host = _factory.Services.GetRequiredService<IHost>();
        var options = new DeliveryOptions();
        options.Headers["x-tenant-id"] = "acme";
        options.Headers["x-user-id"] = "user-77";
        options.Headers["x-correlation-id"] = "corr-77";

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(c => c.SendAsync(new WhoAmI(marker), options));

        var seen = WhoAmIHandler.Seen[marker];
        seen.UserId.ShouldBe("user-77");
        seen.TenantId.ShouldBe("acme");
        var logged = _sink.Events.Single(e => e.MessageTemplate.Text.StartsWith("WhoAmI handled", StringComparison.Ordinal)
                                              && e.Properties["Marker"].ToString().Contains(marker.ToString()));
        logged.Properties["CorrelationId"].ToString().ShouldBe("\"corr-77\"");
        logged.Properties["TenantId"].ToString().ShouldBe("\"acme\"");
        logged.Properties["UserId"].ToString().ShouldBe("\"user-77\"");
    }

    [Fact]
    public async Task Client_that_disconnects_after_the_commit_gets_the_stored_result_on_retry()
    {
        var tenant = NewTenant("abort");
        var key = Guid.NewGuid().ToString();
        using var abort = new CancellationTokenSource();
        _factory.Hook.Client = abort;

        // The client goes away right after the request was written, while the cache is being invalidated.
        try
        {
            await _client.SendAsync(Req(HttpMethod.Post, Requests, NewBody(), tenant, idempotencyKey: key), abort.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var retry = await _client.SendAsync(Req(HttpMethod.Post, Requests, NewBody(), tenant, idempotencyKey: key), Ct);

        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replayed = (await retry.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        var list = await (await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: tenant), Ct))
            .Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct);
        list!.Total.ShouldBe(1, "the retry must not create a second request");
        list.Items.Single().Id.ShouldBe(replayed.Id);
    }

    [Fact]
    public async Task Client_that_disconnects_after_the_submit_commit_gets_202_on_retry_not_412()
    {
        var tenant = NewTenant("abortsub");
        var created = await CreateAsync(tenant);
        var key = Guid.NewGuid().ToString();
        using var abort = new CancellationTokenSource();
        _factory.Hook.Client = abort;
        var url = $"{Requests}/{created.Id}/submit";

        try
        {
            await _client.SendAsync(Req(HttpMethod.Post, url, null, tenant, ifMatch: ETagOf(created), idempotencyKey: key), abort.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var retry = await _client.SendAsync(Req(HttpMethod.Post, url, null, tenant, ifMatch: ETagOf(created), idempotencyKey: key), Ct);

        retry.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await retry.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.Status.ShouldBe(RequestStatus.Submitted);
    }

    [Fact]
    public async Task Put_with_stale_if_match_is_412_problem_details()
    {
        var tenant = NewTenant("stale");
        var created = await CreateAsync(tenant);
        // Another tab saves first.
        var first = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("Monitors"), tenant, ifMatch: ETagOf(created)), Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stale = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("Old tab"), tenant, ifMatch: ETagOf(created)), Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("status").GetInt32().ShouldBe(412);
        // The first save is intact.
        var current = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant: tenant), Ct);
        (await current.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.Title.ShouldBe("Monitors");
    }

    [Fact]
    public async Task Put_without_if_match_is_428()
    {
        var tenant = NewTenant("nomatch");
        var created = await CreateAsync(tenant);

        var response = await _client.SendAsync(Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("x"), tenant), Ct);

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("W/\"{0}\"")]
    [InlineData("{0}")]
    public async Task Put_accepts_weak_and_bare_version_tags(string format)
    {
        var tenant = NewTenant("weak");
        var created = await CreateAsync(tenant);

        var response = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("Monitors"), tenant, ifMatch: string.Format(format, created.Version)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("\"abc\"")]
    [InlineData("\"1\", \"2\"")]
    public async Task Put_with_unusable_if_match_is_412(string ifMatch)
    {
        var tenant = NewTenant("badtag");
        var created = await CreateAsync(tenant);

        var response = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("x"), tenant, ifMatch: ifMatch), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task Put_then_immediate_get_returns_the_new_etag()
    {
        var tenant = NewTenant("fresh");
        var created = await CreateAsync(tenant);
        // Warm the 1 s read cache with the old version.
        var warm = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant: tenant), Ct);
        warm.Headers.ETag!.Tag.ShouldBe(ETagOf(created));
        var listBefore = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: tenant), Ct);
        (await listBefore.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Items.Single().Title.ShouldBe("Laptops");

        var put = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("Monitors"), tenant, ifMatch: ETagOf(created)), Ct);
        var newTag = put.Headers.ETag!.Tag;
        newTag.ShouldNotBe(ETagOf(created));

        var read = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant: tenant), Ct);
        read.Headers.ETag!.Tag.ShouldBe(newTag);
        (await read.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.Title.ShouldBe("Monitors");
        var list = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: tenant), Ct);
        (await list.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Items.Single().Title.ShouldBe("Monitors");
        // The next edit with that tag is not a false 412.
        var next = await _client.SendAsync(
            Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("Again"), tenant, ifMatch: read.Headers.ETag.Tag), Ct);
        next.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Globex_user_gets_404_for_acme_request_even_right_after_acme_read()
    {
        var acme = NewTenant("acme");
        var globex = NewTenant("globex");
        var acmeToken = TestAuth.CreateToken("alice", acme, "requester");
        var globexToken = TestAuth.CreateToken("bob", globex, "requester");
        var created = await CreateAsync(acme, acmeToken);
        var url = $"{Requests}/{created.Id}";

        var acmeRead = await _client.SendAsync(Req(HttpMethod.Get, url, bearer: acmeToken), Ct);
        var globexRead = await _client.SendAsync(Req(HttpMethod.Get, url, bearer: globexToken), Ct);
        var acmeAgain = await _client.SendAsync(Req(HttpMethod.Get, url, bearer: acmeToken), Ct);

        acmeRead.StatusCode.ShouldBe(HttpStatusCode.OK);
        globexRead.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        globexRead.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        acmeAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Writing across tenants is just as invisible.
        var write = await _client.SendAsync(Req(HttpMethod.Put, url, NewBody("mine now"), bearer: globexToken, ifMatch: ETagOf(created)), Ct);
        write.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requester_cannot_read_other_tenant_list()
    {
        var acme = NewTenant("acme");
        var globex = NewTenant("globex");
        var acmeToken = TestAuth.CreateToken("alice", acme, "requester");
        var globexToken = TestAuth.CreateToken("bob", globex, "requester");
        await CreateAsync(acme, acmeToken, "acme one");
        await CreateAsync(acme, acmeToken, "acme two");

        var own = await _client.SendAsync(Req(HttpMethod.Get, Requests, bearer: acmeToken), Ct);
        var other = await _client.SendAsync(Req(HttpMethod.Get, Requests, bearer: globexToken), Ct);
        var viaKey = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: globex), Ct);

        (await own.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!.Total.ShouldBe(2);
        foreach (var response in new[] { other, viaKey })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = (await response.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!;
            page.Items.ShouldBeEmpty();
            page.Total.ShouldBe(0);
        }
    }

    [Fact]
    public async Task List_pages_and_filters_by_status_name()
    {
        var tenant = NewTenant("paging");
        var first = await CreateAsync(tenant, title: "one");
        await CreateAsync(tenant, title: "two");
        await CreateAsync(tenant, title: "three");
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{first.Id}/submit", null, tenant, ifMatch: ETagOf(first), idempotencyKey: Guid.NewGuid().ToString()), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var page = await (await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}?page=1&pageSize=2", tenant: tenant), Ct))
            .Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct);
        var submitted = await (await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}?status=Submitted", tenant: tenant), Ct))
            .Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct);
        var defaults = await (await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: tenant), Ct))
            .Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct);

        page!.Items.Select(i => i.Title).ShouldBe(["three", "two"]);
        page.PageSize.ShouldBe(2);
        page.Total.ShouldBe(3);
        submitted!.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
        defaults!.PageSize.ShouldBe(25);
        (await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}?pageSize=100000", tenant: tenant), Ct))
            .StatusCode.ShouldBe((HttpStatusCode)422);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, Requests), (HttpMethod.Get, $"{Requests}/{Guid.NewGuid()}"),
                     (HttpMethod.Post, Requests), (HttpMethod.Put, $"{Requests}/{Guid.NewGuid()}"),
                     (HttpMethod.Post, $"{Requests}/{Guid.NewGuid()}/submit"),
                 })
        {
            var response = await _client.SendAsync(Req(method, url, apiKey: false), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {url}");
        }
    }

    [Fact]
    public async Task Wrong_api_key_is_401()
    {
        var request = Req(HttpMethod.Get, Requests, tenant: "acme");
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", "not-the-key");

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_without_a_tenant_is_never_a_domain_error()
    {
        // The admin key must say which tenant it acts for: 400. A user token without a tenant claim: 403.
        var noHeader = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var noClaim = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), bearer: TestAuth.CreateToken("alice", null, "requester"), idempotencyKey: Guid.NewGuid().ToString()), Ct);

        noHeader.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noHeader.Content.ReadAsStringAsync(Ct)).ShouldContain("X-Tenant-Id");
        noClaim.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        noClaim.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Role_without_permission_is_403()
    {
        // The admin role may read requests but not create them.
        var admin = TestAuth.CreateToken("root", NewTenant("perm"), "admin");

        var create = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), bearer: admin, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var list = await _client.SendAsync(Req(HttpMethod.Get, Requests, bearer: admin), Ct);

        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_without_idempotency_key_is_400_and_invalid_body_is_400()
    {
        var tenant = NewTenant("invalid");

        var noKey = await _client.SendAsync(Req(HttpMethod.Post, Requests, NewBody(), tenant), Ct);
        var emptyTitle = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(title: ""), tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct);

        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        emptyTitle.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        emptyTitle.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Submitting_a_request_without_lines_is_422_and_missing_request_is_404()
    {
        var tenant = NewTenant("rules");
        var created = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, new { title = "Empty", costCenterCode = "CC-1", supplierId = "s", lines = Array.Empty<object>() }, tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var dto = (await created.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;

        var submit = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{dto.Id}/submit", null, tenant, ifMatch: ETagOf(dto), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var missing = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{Guid.NewGuid()}", tenant: tenant), Ct);

        submit.StatusCode.ShouldBe((HttpStatusCode)422);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task OpenApi_lists_idempotency_and_if_match_headers()
    {
        var response = await _client.GetAsync("/openapi/v1.json", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var paths = doc.GetProperty("paths");

        HeaderNames(paths, "/api/v1/requests", "post").ShouldBe(["Idempotency-Key"]);
        HeaderNames(paths, "/api/v1/requests/{id}", "put").ShouldBe(["If-Match"]);
        HeaderNames(paths, "/api/v1/requests/{id}/submit", "post").Order().ShouldBe(["Idempotency-Key", "If-Match"]);
        HeaderNames(paths, "/api/v1/requests/{id}", "get").ShouldBeEmpty();

        var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
        var apiKey = schemes.GetProperty("ApiKey");
        apiKey.GetProperty("type").GetString().ShouldBe("apiKey");
        apiKey.GetProperty("in").GetString().ShouldBe("header");
        apiKey.GetProperty("name").GetString().ShouldBe("X-Api-Key");
        var oauth = schemes.GetProperty("OAuth2");
        oauth.GetProperty("type").GetString().ShouldBe("oauth2");
        var flow = oauth.GetProperty("flows").GetProperty("authorizationCode");
        flow.GetProperty("authorizationUrl").GetString().ShouldBe(ApiFactory.OpenApiAuthorizationUrl);
        flow.GetProperty("tokenUrl").GetString().ShouldBe(ApiFactory.OpenApiTokenUrl);
        // "email" exists only in the test configuration; "openid" and "profile" come from appsettings.json, so the scopes are configuration.
        flow.GetProperty("scopes").EnumerateObject().Select(o => o.Name).Order().ShouldBe(["email", "openid", "profile"]);
        doc.GetProperty("security").GetArrayLength().ShouldBe(2);
        // Enums are documented as names.
        doc.GetProperty("components").GetProperty("schemas").GetProperty("RequestStatus").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["Draft", "Submitted"]);
    }

    private static string[] HeaderNames(JsonElement paths, string path, string method)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        return operation.TryGetProperty("parameters", out var parameters)
            ? [.. parameters.EnumerateArray()
                .Where(p => p.GetProperty("in").GetString() == "header")
                .Select(p => p.GetProperty("name").GetString()!)]
            : [];
    }
}
