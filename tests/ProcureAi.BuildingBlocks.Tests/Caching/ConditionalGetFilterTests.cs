using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using ProcureAi.BuildingBlocks.Tests.Web;
using ProcureAi.BuildingBlocks.Web.Caching;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Caching;

[Collection(RedisCollection.Name)]
public sealed class ConditionalGetFilterTests(RedisFixture redis) : IAsyncLifetime
{
    private const string Base = "/api/v1/conditional";

    private WebApplication _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _app = await TestHost.StartAsync(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = redis.ConnectionString,
                ["RateLimiting:PermitLimit"] = "100000",
            },
            configure: builder => builder.AddProcureAiCaching());

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static HttpRequestMessage Get(string path, string tenant = "acme", string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", tenant);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
        await _app.GetTestClient().SendAsync(request, Ct);

    [Fact]
    public async Task Get_returns_weak_etag_and_no_cache_headers()
    {
        using var response = await SendAsync(Get($"{Base}/plain"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = response.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeTrue();
        // 16 bytes of SHA-256 in base64url: 22 characters, no padding.
        etag.Tag.Trim('"').Length.ShouldBe(22);
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.NoCache.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("plain");
    }

    [Fact]
    public async Task Same_body_gives_the_same_etag()
    {
        using var first = await SendAsync(Get($"{Base}/plain"));
        using var second = await SendAsync(Get($"{Base}/plain"));

        second.Headers.ETag!.ToString().ShouldBe(first.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("star")]
    [InlineData("list")]
    [InlineData("strong-form-of-weak")]
    public async Task Matching_if_none_match_returns_304_without_body(string form)
    {
        using var first = await SendAsync(Get($"{Base}/plain"));
        var tag = first.Headers.ETag!.ToString();
        var header = form switch
        {
            "exact" => tag,
            "star" => "*",
            "list" => $"\"other\", {tag}, W/\"third\"",
            _ => tag[2..],
        };

        using var response = await SendAsync(Get($"{Base}/plain", ifNoneMatch: header));

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag!.ToString().ShouldBe(tag);
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_matching_if_none_match_returns_the_body()
    {
        using var response = await SendAsync(Get($"{Base}/plain", ifNoneMatch: "W/\"nope\""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("plain");
    }

    [Fact]
    public async Task Version_etag_from_controller_is_kept()
    {
        using var response = await SendAsync(Get($"{Base}/versioned"));

        response.Headers.ETag!.ToString().ShouldBe("\"7\"");
        using var again = await SendAsync(Get($"{Base}/versioned", ifNoneMatch: "\"7\""));
        again.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        again.Headers.ETag!.ToString().ShouldBe("\"7\"");
    }

    [Fact]
    public async Task Get_varies_by_cookie()
    {
        using var plain = await SendAsync(Get($"{Base}/plain"));
        using var versioned = await SendAsync(Get($"{Base}/versioned"));
        using var notModified = await SendAsync(Get($"{Base}/versioned", ifNoneMatch: "\"7\""));

        foreach (var response in new[] { plain, versioned, notModified })
        {
            response.Headers.Vary.ShouldContain("Cookie");
        }
    }

    [Fact]
    public async Task Same_version_tag_in_two_tenants_is_safe_only_because_of_Vary_Cookie_and_login_clearing()
    {
        // The server alone cannot tell the tenants apart here: a version tag ("7") is the same for both, so tenant B's
        // If-None-Match "7" matches and gets 304. What protects tenant B from tenant A's cached body is the browser side:
        // Vary: Cookie (an entry stored under another session cookie is never reused) and Clear-Site-Data on login/logout
        // (gateway tests). This test pins the server half: tagged responses carry Vary: Cookie.
        using var tenantA = await SendAsync(Get($"{Base}/versioned", "acme"));
        using var tenantB = await SendAsync(Get($"{Base}/versioned", "globex", ifNoneMatch: tenantA.Headers.ETag!.ToString()));

        tenantA.Headers.ETag!.ToString().ShouldBe("\"7\"");
        tenantB.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        tenantA.Headers.Vary.ShouldContain("Cookie");
        tenantB.Headers.Vary.ShouldContain("Cookie");
    }

    [Fact]
    public async Task Different_user_with_other_etag_gets_200()
    {
        using var acme = await SendAsync(Get($"{Base}/plain", "acme"));
        var acmeTag = acme.Headers.ETag!.ToString();

        using var globex = await SendAsync(Get($"{Base}/plain", "globex", ifNoneMatch: acmeTag));

        globex.StatusCode.ShouldBe(HttpStatusCode.OK);
        globex.Headers.ETag!.ToString().ShouldNotBe(acmeTag);
        (await globex.Content.ReadAsStringAsync(Ct)).ShouldContain("globex");
    }

    [Fact]
    public async Task Error_responses_get_no_etag()
    {
        using var response = await SendAsync(Get($"{Base}/missing"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.ETag.ShouldBeNull();
        (response.Headers.CacheControl?.NoCache ?? false).ShouldBeFalse();
    }

    [Fact]
    public async Task Writes_get_no_etag()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/plain-post");
        request.Headers.Add("X-Api-Key", TestAuth.ApiKey);
        request.Headers.Add("X-Tenant-Id", "acme");

        using var response = await SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldBeNull();
    }

    [Fact]
    public async Task Cached_replay_still_answers_304()
    {
        var id = Guid.NewGuid().ToString("N");
        using var first = await SendAsync(Get($"{Base}/cached?id={id}"));
        var tag = first.Headers.ETag!.ToString();

        using var replay = await SendAsync(Get($"{Base}/cached?id={id}", ifNoneMatch: tag));

        // The second call was served from the cache and still answers 304.
        ExecutionCounter.Get(id).ShouldBe(1);
        replay.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        replay.Headers.ETag!.ToString().ShouldBe(tag);
        (await replay.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task File_result_is_not_buffered_and_has_no_etag()
    {
        using var response = await SendAsync(Get($"{Base}/file"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldBeNull();
        response.Headers.CacheControl.ShouldBeNull();
        response.Headers.Vary.ShouldNotContain("Cookie");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("%PDF-fake");
    }

    [Fact]
    public async Task Event_stream_response_is_not_buffered()
    {
        ConditionalController.EventStreamGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Buffered by mistake, the headers would not arrive until the action ends, which it never does: fail instead of hanging.
        using var response = await _app.GetTestClient()
            .SendAsync(Get($"{Base}/events"), HttpCompletionOption.ResponseHeadersRead, Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        response.Headers.ETag.ShouldBeNull();
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(stream);
        // The first event arrives while the action is still waiting: nothing is held back until the response ends.
        (await reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBe("data: one");
        ConditionalController.EventStreamGate.SetResult();
        await reader.ReadToEndAsync(Ct);
    }
}
