using ProcureAi.BuildingBlocks.Application;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.BuildingBlocks.Web.Proxies;
using ProcureAi.BuildingBlocks.Web.RateLimiting;
using ProcureAi.Contracts.Access;
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

    private readonly CapturingSink _sink = CapturingSink.Shared;
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
        supplierId = "contoso-hardware",
        lines = new[] { new { sku = "LAP-1", quantity = 3 } },
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
    public async Task Request_dto_names_the_requester_on_create_get_and_list()
    {
        var tenant = NewTenant("requester");
        var alice = TestAuth.CreateToken("alice", tenant, "requester");
        var bob = TestAuth.CreateToken("bob", tenant, "requester");

        var created = await CreateAsync(tenant, alice);
        var asBob = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant: tenant, bearer: bob), Ct);
        var list = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: tenant, bearer: bob), Ct);

        created.RequesterId.ShouldBe("alice", "the creator, not the reader");
        (await asBob.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!.RequesterId.ShouldBe("alice");
        (await list.Content.ReadAsStringAsync(Ct)).ShouldContain("\"requesterId\":\"alice\"");
    }

    [Fact]
    public async Task List_shows_the_whole_tenant_to_an_admin_and_only_the_scope_to_a_requester()
    {
        var tenant = NewTenant("allist");
        infra.Access.SetCostCenters(tenant, "alice", "CC-100");
        infra.Access.SetCostCenters(tenant, "tom", "IT-200");
        infra.Access.SetCostCenters(tenant, "ada");
        var alice = TestAuth.CreateToken("alice", tenant, "requester");
        var tom = TestAuth.CreateToken("tom", tenant, "requester");
        var admin = TestAuth.CreateToken("ada", tenant, "admin");
        var aliceRequest = await CreateAsync(tenant, alice, "Alice's");
        using var created = await _client.SendAsync(Req(HttpMethod.Post, Requests,
            new { title = "Tom's", costCenterCode = "IT-200", supplierId = "contoso-hardware", lines = new[] { new { sku = "LAP-1", quantity = 1 } } },
            tenant, tom, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var tomRequest = (await created.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;

        async Task<string[]> TitlesFor(string bearer)
        {
            using var response = await _client.SendAsync(Req(HttpMethod.Get, Requests, tenant: tenant, bearer: bearer), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = (await response.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!;
            return [.. page.Items.Select(i => i.Title).Order()];
        }

        // The admin role holds admin.authorization (no cost centers of their own): the whole tenant.
        (await TitlesFor(admin)).ShouldBe(["Alice's", "Tom's"]);
        (await TitlesFor(tom)).ShouldBe(["Tom's"]);
        (await TitlesFor(alice)).ShouldBe(["Alice's"]);
        aliceRequest.Id.ShouldNotBe(tomRequest.Id);
    }

    [Fact]
    public async Task Connection_string_handed_to_EF_and_Wolverine_carries_the_configured_max_pool_size()
    {
        await using var capped = new ApiFactory(infra, null, new Dictionary<string, string?> { ["Database:MaxPoolSize"] = "7" });
        using var client = capped.CreateClient();

        // What ServiceHost hands to EF Core, Wolverine (it reads the configuration), Marten and the readiness check.
        var configured = capped.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:requestsdb"]!;
        new Npgsql.NpgsqlConnectionStringBuilder(configured).MaxPoolSize.ShouldBe(7);

        // And the pool Wolverine really opened: its message store builds its own NpgsqlDataSource (NpgsqlDataSource.Create).
        var store = capped.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>().Storage;
        var dataSources = FindDataSources(store).ToList();
        dataSources.ShouldNotBeEmpty("Wolverine's Postgres message store holds its own data source");
        dataSources.ShouldAllBe(d => new Npgsql.NpgsqlConnectionStringBuilder(d.ConnectionString).MaxPoolSize == 7);
    }

    private static IEnumerable<Npgsql.NpgsqlDataSource> FindDataSources(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Value, int Depth)>([(root, 0)]);
        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (!seen.Add(value))
            {
                continue;
            }

            if (value is Npgsql.NpgsqlDataSource found)
            {
                yield return found;
                continue;
            }

            if (depth >= 3 || !(value.GetType().Namespace ?? "").StartsWith("Wolverine", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var field in value.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (field.GetValue(value) is { } next && !field.FieldType.IsPrimitive && field.FieldType != typeof(string))
                {
                    queue.Enqueue((next, depth + 1));
                }
            }
        }
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
        headers["x-calling-service"].ShouldBe("requests-api");
        headers["x-correlation-id"].ShouldBe(correlationId);
        headers.Keys.ShouldNotContain(k => k.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_message_names_requests_api_as_sender_whoever_called(bool adminKeyClaimingAnotherService)
    {
        var tenant = NewTenant("sender");
        var user = TestAuth.CreateToken("alice", tenant, "requester");
        // Only the requester submits: Alice's own draft for the user case, the key's own draft for the key case.
        var created = adminKeyClaimingAnotherService ? await CreateAsync(tenant) : await CreateAsync(tenant, user);
        var probe = new RabbitProbe(infra.RabbitConnectionString);
        var queue = await probe.BindQueueAsync("requests");
        var request = adminKeyClaimingAnotherService
            ? Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", null, tenant, ifMatch: ETagOf(created), idempotencyKey: Guid.NewGuid().ToString())
            : Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", null, bearer: user, ifMatch: ETagOf(created), idempotencyKey: Guid.NewGuid().ToString());
        if (adminKeyClaimingAnotherService)
        {
            request.Headers.Add("X-Calling-Service", "approvals-api");
        }

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await probe.WaitForCountAsync(queue, 1, TimeSpan.FromSeconds(30));
        var headers = await probe.TakeHeadersAsync(queue);
        // The sender is this service, not the HTTP caller's claim (a normal user has none; the key may claim any).
        headers["x-calling-service"].ShouldBe("requests-api");
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
        // The log event is written by the handler itself; poll briefly so a slow runner cannot make this a race.
        Serilog.Events.LogEvent? logged = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (logged is null && DateTime.UtcNow < deadline)
        {
            logged = _sink.Events.FirstOrDefault(e => e.MessageTemplate.Text.StartsWith("WhoAmI handled", StringComparison.Ordinal)
                                                      && e.Properties["Marker"].ToString().Contains(marker.ToString()));
            if (logged is null)
            {
                await Task.Delay(100);
            }
        }
        logged.ShouldNotBeNull($"no 'WhoAmI handled' log event for {marker}; newest templates: "
                               + string.Join(" | ", _sink.Events.TakeLast(5).Select(e => e.MessageTemplate.Text)));
        logged.Properties["CorrelationId"].ToString().ShouldBe("\"corr-77\"");
        logged.Properties["TenantId"].ToString().ShouldBe("\"acme\"");
        logged.Properties["UserId"].ToString().ShouldBe("\"user-77\"");
    }

    private async Task<(string Title, string ETag)> ReadAsync(HttpClient client, Guid id, string tenant)
    {
        var response = await client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{id}", tenant: tenant), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        return (dto.Title, response.Headers.ETag!.Tag);
    }

    private static DeliveryOptions HeadersFor(string tenant)
    {
        var options = new DeliveryOptions();
        options.Headers["x-tenant-id"] = tenant;
        options.Headers["x-user-id"] = "user-1";
        return options;
    }

    [Fact]
    public async Task Message_handler_write_evicts_cached_get()
    {
        // A 60 s GET cache: only an eviction, not expiry, can make the next read fresh.
        await using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Caching:DefaultGetSeconds", "60"));
        using var client = factory.CreateClient();
        var tenant = NewTenant("msg-evict");
        var created = (await (await client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct)).Content
            .ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        var before = await ReadAsync(client, created.Id, tenant);
        (await ReadAsync(client, created.Id, tenant)).ShouldBe(before, "the second read is served from the cache");

        // A reader that arrives after the handler asked for the eviction but before its transaction commits sees (and caches)
        // the old row. Evicting at that point would leave the old row cached; evicting after the commit does not.
        RenameViaMessageHandler.BeforeCommit = async () => (await ReadAsync(client, created.Id, tenant)).ETag.ShouldBe(before.ETag);
        var host = factory.Services.GetRequiredService<IHost>();
        try
        {
            await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
                .ExecuteAndWaitAsync(c => c.SendAsync(new RenameViaMessage(created.Id, "Renamed by message"), HeadersFor(tenant)));
        }
        finally
        {
            RenameViaMessageHandler.BeforeCommit = null;
        }

        var after = await ReadAsync(client, created.Id, tenant);
        after.Title.ShouldBe("Renamed by message");
        after.ETag.ShouldNotBe(before.ETag);
    }

    [Fact]
    public async Task Message_handler_that_fails_after_asking_for_eviction_evicts_nothing()
    {
        // The eviction is part of the handler's outgoing messages: a rollback discards it with the write.
        await using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Caching:DefaultGetSeconds", "60"));
        using var client = factory.CreateClient();
        var tenant = NewTenant("msg-fail");
        var created = (await (await client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct)).Content
            .ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        var before = await ReadAsync(client, created.Id, tenant);
        // The create call above evicted this request's tags itself; only evictions after this point count.
        factory.Services.GetRequiredService<CancelAfterCommit>().Evicted.Clear();

        var host = factory.Services.GetRequiredService<IHost>();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30)).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.SendAsync(new RenameViaMessage(created.Id, "Never committed", true), HeadersFor(tenant)));

        (await ReadAsync(client, created.Id, tenant)).ShouldBe(before);
        // The row did not change, so the read above cannot tell an eviction from none: check what was evicted.
        factory.Services.GetRequiredService<CancelAfterCommit>().Evicted
            .Where(tag => tag.Contains(created.Id.ToString("D"), StringComparison.Ordinal)).ShouldBeEmpty();
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
    }

    private async Task<PagedResult<RequestDto>> ListAsync(string query, string tenant, string? bearer = null)
    {
        using var response = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}?{query}", tenant: tenant, bearer: bearer), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PagedResult<RequestDto>>(Json, Ct))!;
    }

    [Fact]
    public async Task Mine_lists_only_callers_requests()
    {
        var tenant = NewTenant("mine");
        infra.Access.SetCostCenters(tenant, "alice", "CC-100");
        infra.Access.SetCostCenters(tenant, "bob", "CC-100");
        var alice = TestAuth.CreateToken("alice", tenant, "requester");
        var bob = TestAuth.CreateToken("bob", tenant, "requester");
        await CreateAsync(tenant, alice, "alice one");
        await CreateAsync(tenant, alice, "alice two");
        await CreateAsync(tenant, bob, "bob one");

        var aliceMine = await ListAsync("mine=true", tenant, alice);
        var bobMine = await ListAsync("mine=true", tenant, bob);
        var aliceAll = await ListAsync("mine=false", tenant, alice);

        aliceMine.Items.Select(i => i.Title).ShouldBe(["alice two", "alice one"]);
        aliceMine.Total.ShouldBe(2);
        bobMine.Items.Select(i => i.Title).ShouldBe(["bob one"]);
        aliceAll.Total.ShouldBe(3, "without mine the list is the tenant's");
    }

    [Fact]
    public async Task Open_group_excludes_completed_rejected_cancelled()
    {
        var tenant = NewTenant("groups");
        var draft = await CreateAsync(tenant, title: "draft");
        var submitted = await CreateAsync(tenant, title: "submitted");
        var cancelled = await CreateAsync(tenant, title: "cancelled");
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{submitted.Id}/submit", null, tenant, ifMatch: ETagOf(submitted), idempotencyKey: Guid.NewGuid().ToString()), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{cancelled.Id}/cancel", null, tenant, ifMatch: ETagOf(cancelled), idempotencyKey: Guid.NewGuid().ToString()), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var open = await ListAsync("status=open", tenant);
        var closed = await ListAsync("status=closed", tenant);
        var one = await ListAsync("status=cancelled", tenant);

        open.Items.Select(i => i.Title).ShouldBe(["submitted", "draft"]);
        closed.Items.Select(i => i.Title).ShouldBe(["cancelled"]);
        one.Items.Select(i => i.Title).ShouldBe(["cancelled"]);
        open.Items.ShouldNotContain(i => i.Status == RequestStatus.Cancelled || i.Status == RequestStatus.Completed || i.Status == RequestStatus.Rejected);
        draft.Status.ShouldBe(RequestStatus.Draft);
    }

    [Fact]
    public async Task Unknown_status_is_a_400_validation_problem()
    {
        using var response = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}?status=bogus", tenant: NewTenant("badstatus")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_and_cancel_without_if_match_return_428()
    {
        var tenant = NewTenant("nomatch2");
        var created = await CreateAsync(tenant);

        var submit = await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", null, tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var cancel = await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", null, tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct);

        submit.StatusCode.ShouldBe((HttpStatusCode)428);
        cancel.StatusCode.ShouldBe((HttpStatusCode)428);
    }

    [Fact]
    public async Task Submit_and_cancel_with_stale_if_match_return_412_and_get_carries_the_version_etag()
    {
        var tenant = NewTenant("stale2");
        var created = await CreateAsync(tenant);
        (await _client.SendAsync(Req(HttpMethod.Put, $"{Requests}/{created.Id}", NewBody("Edited"), tenant, ifMatch: ETagOf(created)), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var submit = await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{created.Id}/submit", null, tenant, ifMatch: ETagOf(created), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var cancel = await _client.SendAsync(Req(HttpMethod.Post, $"{Requests}/{created.Id}/cancel", null, tenant, ifMatch: ETagOf(created), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var read = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}/{created.Id}", tenant: tenant), Ct);

        submit.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        cancel.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var current = (await read.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        read.Headers.ETag!.Tag.ShouldBe(ETagOf(current));
    }

    [Theory]
    [InlineData("pageSize=100000", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("page=-3", "page")]
    public async Task Invalid_paging_is_a_400_validation_problem(string query, string field)
    {
        using var response = await _client.SendAsync(Req(HttpMethod.Get, $"{Requests}?{query}", tenant: NewTenant("paging")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public void Visible_appsettings_sections_equal_the_code_defaults()
    {
        // appsettings.json lists the defaults so they are visible per environment; they must not drift from the code.
        var sp = _factory.Services;
        T Bound<T>() where T : class => sp.GetRequiredService<IOptions<T>>().Value;

        Bound<CachingOptions>().ShouldBeEquivalentTo(new CachingOptions());
        var idempotency = Bound<IdempotencyOptions>();
        idempotency.Retention.ShouldBe(new IdempotencyOptions().Retention); // WaitTimeout is raised by this factory
        idempotency.FailureShare.ShouldBe(new IdempotencyOptions().FailureShare);
        idempotency.MaxKeyLength.ShouldBe(new IdempotencyOptions().MaxKeyLength);
        idempotency.RetryAfterSeconds.ShouldBe(new IdempotencyOptions().RetryAfterSeconds);
        var fusion = Bound<FusionCacheSettings>();
        fusion.DefaultDuration.ShouldBe(new FusionCacheSettings().DefaultDuration);
        fusion.DistributedTtlPadding.ShouldBe(new FusionCacheSettings().DistributedTtlPadding);
        Bound<RateLimitingOptions>().ShouldBeEquivalentTo(new RateLimitingOptions());
        var forwarded = Bound<ForwardedHeadersSettings>();
        forwarded.ForwardLimit.ShouldBe(new ForwardedHeadersSettings().ForwardLimit);
        forwarded.Headers.ShouldBe(new ForwardedHeadersSettings().Headers);
        forwarded.HasTrustedProxies.ShouldBeFalse();
    }

    [Fact]
    public async Task Health_ready_is_200_while_postgres_rabbitmq_and_redis_are_up()
    {
        using var response = await _client.GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
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
    public async Task Requester_without_requests_create_in_matrix_gets_403()
    {
        // The matrix lives in Access: this tenant's requesters may only read.
        var tenant = NewTenant("matrix");
        infra.Access.SetMatrix(tenant, new() { ["requester"] = [Permissions.RequestsRead] });
        var requester = TestAuth.CreateToken("rita", tenant, "requester");

        var create = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), bearer: requester, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        var list = await _client.SendAsync(Req(HttpMethod.Get, Requests, bearer: requester), Ct);

        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requester_can_create_and_submit_only_on_their_own_cost_centers_but_the_admin_key_can_on_any()
    {
        var tenant = NewTenant("ownercc");
        infra.Access.SetCostCenters(tenant, "rita", "IT-200");
        var rita = TestAuth.CreateToken("rita", tenant, "requester");

        // CC-100 (NewBody) is not Rita's: 403, nothing created.
        var foreign = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), bearer: rita, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The admin key drafts on CC-100 (as Claude does); Rita may not submit it ...
        var draft = await CreateAsync(tenant);
        var submit = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{draft.Id}/submit", tenant: tenant, bearer: rita, ifMatch: ETagOf(draft), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        submit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // ... but she can create and submit on IT-200, and so can the key.
        var own = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, new { title = "Own", costCenterCode = "IT-200", supplierId = "contoso-hardware", lines = new[] { new { sku = "A", quantity = 1 } } },
                bearer: rita, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        own.StatusCode.ShouldBe(HttpStatusCode.Created);
        var ownDto = (await own.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
        var ownSubmit = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{ownDto.Id}/submit", bearer: rita, ifMatch: ETagOf(ownDto), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        ownSubmit.IsSuccessStatusCode.ShouldBeTrue(await ownSubmit.Content.ReadAsStringAsync(Ct));
        var keySubmit = await _client.SendAsync(
            Req(HttpMethod.Post, $"{Requests}/{draft.Id}/submit", tenant: tenant, ifMatch: ETagOf(draft), idempotencyKey: Guid.NewGuid().ToString()), Ct);
        keySubmit.IsSuccessStatusCode.ShouldBeTrue(await keySubmit.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Nobody_but_the_requester_can_edit_or_submit_a_draft_not_a_manager_and_not_another_demo_user()
    {
        var tenant = NewTenant("ownerdraft");
        infra.Access.SetCostCenters(tenant, "tom", "IT-200");
        infra.Access.SetCostCenters(tenant, "maria", "IT-200");
        infra.Access.SetCostCenters(tenant, "demo-a", "DEMO-100");
        infra.Access.SetCostCenters(tenant, "demo-b", "DEMO-100");
        var draftBody = new { title = "Mine", costCenterCode = "IT-200", supplierId = "contoso-hardware", lines = new[] { new { sku = "A", quantity = 1 } } };
        var tomDraft = await CreateWithAsync(draftBody, TestAuth.CreateToken("tom", tenant, "requester"));
        var demoDraft = await CreateWithAsync(
            new { title = "Demo", costCenterCode = "DEMO-100", supplierId = "contoso-hardware", lines = draftBody.lines }, TestAuth.CreateToken("demo-a", tenant, "requester"));

        foreach (var (draft, intruder) in new[] { (tomDraft, "maria"), (demoDraft, "demo-b") })
        {
            var token = TestAuth.CreateToken(intruder, tenant, intruder == "maria" ? "manager" : "requester");
            var put = await _client.SendAsync(
                Req(HttpMethod.Put, $"{Requests}/{draft.Id}", new { title = "Taken over", lines = draftBody.lines }, bearer: token, ifMatch: ETagOf(draft)), Ct);
            var submit = await _client.SendAsync(
                Req(HttpMethod.Post, $"{Requests}/{draft.Id}/submit", bearer: token, ifMatch: ETagOf(draft), idempotencyKey: Guid.NewGuid().ToString()), Ct);
            put.StatusCode.ShouldBe(HttpStatusCode.Forbidden, intruder);
            submit.StatusCode.ShouldBe(HttpStatusCode.Forbidden, intruder);
        }
    }

    private async Task<RequestDto> CreateWithAsync(object body, string bearer)
    {
        var response = await _client.SendAsync(
            Req(HttpMethod.Post, Requests, body, bearer: bearer, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RequestDto>(Json, Ct))!;
    }

    [Fact]
    public async Task AccessChanged_on_the_access_exchange_evicts_the_cached_permissions()
    {
        var tenant = NewTenant("evict");
        infra.Access.SetMatrix(tenant, new() { ["requester"] = [Permissions.RequestsRead] });
        var requester = TestAuth.CreateToken("rita", tenant, "requester");
        Task<HttpResponseMessage> Create() => _client.SendAsync(
            Req(HttpMethod.Post, Requests, NewBody(), bearer: requester, idempotencyKey: Guid.NewGuid().ToString()), Ct);
        (await Create()).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Access grants requests.create. The permissions are cached for minutes, so it is still 403 ...
        infra.Access.SetMatrix(tenant, new() { ["requester"] = [Permissions.RequestsRead, Permissions.RequestsCreate] });
        (await Create()).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // ... until Access announces the change on its exchange, as the real Access does.
        var bus = _factory.Services.GetRequiredService<IMessageBus>();
        await bus.EndpointFor(new Uri("rabbitmq://exchange/access"))
            .SendAsync(new AccessChanged(tenant, "Matrix", null, DateTimeOffset.UtcNow));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        HttpStatusCode status;
        do
        {
            await Task.Delay(200, Ct);
            using var response = await Create();
            status = response.StatusCode;
        }
        while (status == HttpStatusCode.Forbidden && DateTime.UtcNow < deadline);

        status.ShouldBe(HttpStatusCode.Created);
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
            Req(HttpMethod.Post, Requests, new { title = "Empty", costCenterCode = "CC-1", supplierId = "contoso-hardware", lines = Array.Empty<object>() }, tenant, idempotencyKey: Guid.NewGuid().ToString()), Ct);
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
        HeaderNames(paths, "/api/v1/requests/{id}/cancel", "post").Order().ShouldBe(["Idempotency-Key", "If-Match"]);
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
        flow.GetProperty("scopes").EnumerateObject().Select(o => o.Name).Order().ShouldBe(["email", "openid", "organization", "profile"]);
        doc.GetProperty("security").GetArrayLength().ShouldBe(2);
        // Enums are documented as names.
        doc.GetProperty("components").GetProperty("schemas").GetProperty("RequestStatus").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["Draft", "Submitted", "Approved", "Ordered", "Completed", "Rejected", "Cancelling", "Cancelled"]);
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
