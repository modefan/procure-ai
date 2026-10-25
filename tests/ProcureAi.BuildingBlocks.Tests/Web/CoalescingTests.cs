using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using ProcureAi.BuildingBlocks.Web.Caching;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Two test hosts share one Redis, standing in for two instances of the same service.</summary>
[Collection(RedisCollection.Name)]
public sealed class CoalescingTests(RedisFixture redis) : IAsyncLifetime
{
    private WebApplication _a = null!;
    private WebApplication _b = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;

    public async ValueTask InitializeAsync()
    {
        _a = await StartInstanceAsync("00:00:10");
        _b = await StartInstanceAsync("00:00:10");
        _clientA = Client(_a);
        _clientB = Client(_b);
    }

    public async ValueTask DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private async Task<WebApplication> StartInstanceAsync(string waitTimeout) =>
        await TestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["Idempotency:WaitTimeout"] = waitTimeout,
                // These tests fire hundreds of requests as one caller; the default limit (100 per second) would turn a fast run into 429s.
                ["RateLimiting:PermitLimit"] = "100000",
            },
            configure: builder => builder.AddProcureAiCaching());

    private static HttpClient Client(WebApplication app, string tenant = "acme")
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", TestAuth.ApiKey);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static HttpRequestMessage Post(string id, string key, CoalescingController.PostRequest? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/coalesce/post")
        {
            Content = JsonContent.Create(body ?? new CoalescingController.PostRequest(id)),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task Ten_parallel_identical_gets_across_two_instances_execute_once()
    {
        var id = NewId();
        var tasks = Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? _clientA : _clientB).GetStringAsync($"/api/v1/coalesce/get?id={id}", TestContext.Current.CancellationToken)).ToArray();
        var bodies = await Task.WhenAll(tasks);

        ExecutionCounter.Get(id).ShouldBe(1);
        bodies.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Cached_get_is_per_tenant()
    {
        var id = NewId();
        using var acme = Client(_a, "acme");
        using var globex = Client(_a, "globex");

        var first = await acme.GetStringAsync($"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken);
        var second = await globex.GetStringAsync($"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken);

        ExecutionCounter.Get(id).ShouldBe(2);
        first.ShouldNotBe(second);
        first.ShouldContain("acme");
        second.ShouldContain("globex");
    }

    [Fact]
    public async Task Cache_expires_after_one_second()
    {
        var id = NewId();
        await _clientA.GetStringAsync($"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken);
        await _clientB.GetStringAsync($"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken);
        ExecutionCounter.Get(id).ShouldBe(1);

        await Task.Delay(1500, TestContext.Current.CancellationToken);
        await _clientA.GetStringAsync($"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken);

        ExecutionCounter.Get(id).ShouldBe(2);
    }

    [Fact]
    public async Task Query_order_does_not_change_the_cache_key()
    {
        var id = NewId();
        await _clientA.GetStringAsync($"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken);
        await _clientA.GetStringAsync($"/api/v1/coalesce/get?delayMs=0&id={id}", TestContext.Current.CancellationToken);

        ExecutionCounter.Get(id).ShouldBe(1);
    }

    [Fact]
    public async Task Failed_get_is_not_cached()
    {
        var id = NewId();
        using var first = await _clientA.GetAsync($"/api/v1/coalesce/broken?id={id}", TestContext.Current.CancellationToken);
        using var second = await _clientA.GetAsync($"/api/v1/coalesce/broken?id={id}", TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        ExecutionCounter.Get(id).ShouldBe(2);
    }

    [Fact]
    public async Task Ten_parallel_posts_same_key_execute_once_and_return_identical_responses()
    {
        var id = NewId();
        var key = NewId();
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var response = await (i % 2 == 0 ? _clientA : _clientB).SendAsync(Post(id, key), TestContext.Current.CancellationToken);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                Location: response.Headers.Location?.ToString(), ETag: response.Headers.ETag?.ToString());
        }).ToArray();
        var results = await Task.WhenAll(tasks);

        ExecutionCounter.Get(id).ShouldBe(1);
        results.Distinct().Count().ShouldBe(1);
        results[0].StatusCode.ShouldBe(HttpStatusCode.Created);
        results[0].Location.ShouldBe("/api/v1/things/" + id);
        results[0].ETag.ShouldBe("\"v1\"");
    }

    [Fact]
    public async Task Same_key_different_body_is_422()
    {
        var id = NewId();
        var key = NewId();
        using var first = await _clientA.SendAsync(Post(id, key, new CoalescingController.PostRequest(id, 0)), TestContext.Current.CancellationToken);
        using var second = await _clientB.SendAsync(
            Post(id, key, new CoalescingController.PostRequest(id, 0, Payload: "different")), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        ExecutionCounter.Get(id).ShouldBe(1);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_400()
    {
        var id = NewId();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/coalesce/post")
        {
            Content = JsonContent.Create(new CoalescingController.PostRequest(id, 0)),
        };
        using var response = await _clientA.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ExecutionCounter.Get(id).ShouldBe(0);
    }

    [Fact]
    public async Task Waiter_times_out_with_503_and_retry_after()
    {
        await using var slow = await StartInstanceAsync("00:00:01");
        using var slowClient = Client(slow);
        var id = NewId();
        var key = NewId();
        var body = new CoalescingController.PostRequest(id, 3000);

        var first = slowClient.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        using var waiter = await slowClient.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);

        waiter.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        waiter.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(1));
        using var firstResponse = await first;
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        ExecutionCounter.Get(id).ShouldBe(1);
    }

    [Fact]
    public async Task Failed_first_call_is_not_stored()
    {
        var id = NewId();
        var key = NewId();
        var body = new CoalescingController.PostRequest(id, 0, FailFirst: true);

        using var first = await _clientA.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);
        using var retry = await _clientB.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        ExecutionCounter.Get(id).ShouldBe(2);
    }

    [Fact]
    public async Task Waiters_on_a_failed_first_call_get_the_same_error_without_running_again()
    {
        var id = NewId();
        var key = NewId();
        var body = new CoalescingController.PostRequest(id, 800, FailFirst: true);

        var first = _clientA.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        using var waiter = await _clientB.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);
        using var firstResponse = await first;

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        waiter.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ExecutionCounter.Get(id).ShouldBe(1);
    }

    [Fact]
    public async Task Client_disconnect_after_the_action_ran_does_not_lose_the_stored_result()
    {
        var id = NewId();
        var key = NewId();
        var body = new CoalescingController.PostRequest(id, 1500);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var dropped = await Should.ThrowAsync<OperationCanceledException>(
            () => _clientA.SendAsync(Post(id, key, body), cts.Token));
        dropped.ShouldNotBeNull();

        // The action keeps running after the client left; the retry waits for it and gets its stored result.
        using var retry = await _clientB.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);

        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.ETag!.ToString().ShouldBe("\"v1\"");
        ExecutionCounter.Get(id).ShouldBe(1);
    }

    [Fact]
    public async Task Conflict_responses_are_not_stored()
    {
        var id = NewId();
        var key = NewId();
        var body = new CoalescingController.PostRequest(id, 0, ConflictFirst: true);

        using var first = await _clientA.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);
        using var retry = await _clientB.SendAsync(Post(id, key, body), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        ExecutionCounter.Get(id).ShouldBe(2);
    }

    [Fact]
    public async Task Get_waiter_on_another_instance_shares_the_failure_and_a_fresh_call_runs_again()
    {
        var id = NewId();
        var first = _clientA.GetAsync($"/api/v1/coalesce/fail-slow?id={id}", TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        using var waiter = await _clientB.GetAsync($"/api/v1/coalesce/fail-slow?id={id}", TestContext.Current.CancellationToken);
        using var firstResponse = await first;

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        waiter.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ExecutionCounter.Get(id).ShouldBe(1);

        using var fresh = await _clientB.GetAsync($"/api/v1/coalesce/fail-slow?id={id}", TestContext.Current.CancellationToken);

        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        ExecutionCounter.Get(id).ShouldBe(2);
    }

    [Fact]
    public async Task Fast_parallel_gets_across_two_instances_execute_once_every_time()
    {
        // No action delay: a waiter takes the lock the moment the runner releases it, which exposes any gap
        // between releasing the lock and the result being readable by the other instance.
        for (var round = 0; round < 10; round++)
        {
            var id = NewId();
            var tasks = Enumerable.Range(0, 40).Select(i =>
                (i % 2 == 0 ? _clientA : _clientB).GetStringAsync(
                    $"/api/v1/coalesce/get?id={id}&delayMs=0", TestContext.Current.CancellationToken)).ToArray();
            var bodies = await Task.WhenAll(tasks);

            ExecutionCounter.Get(id).ShouldBe(1, $"round {round}");
            bodies.Distinct().Count().ShouldBe(1);
        }
    }
}
