using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>
/// Who shares a rate-limit allowance: an authenticated user and tenant has their own (so many users behind one
/// gateway are not throttled as one IP), anonymous callers share their client IP, and a forwarded client IP counts
/// only when the connection comes from a trusted proxy.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class RateLimitPartitionTests
{
    private const string Me = "/api/v1/auth-test/me";
    private const string Ping = "/api/v1/test/ping";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Limit(int permits) => new()
    {
        ["RateLimiting:PermitLimit"] = permits.ToString(),
        ["RateLimiting:WindowSeconds"] = "60",
    };

    private static HttpRequestMessage Get(string url, string? bearer = null, string? remoteIp = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (remoteIp is not null)
        {
            request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return request;
    }

    private static async Task<List<HttpStatusCode>> SendAsync(HttpClient client, int count, Func<HttpRequestMessage> request)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add((await client.SendAsync(request(), Ct)).StatusCode);
        }

        return statuses;
    }

    [Fact]
    public async Task Each_authenticated_user_has_their_own_allowance_behind_one_ip()
    {
        await using var app = await TestHost.StartAsync(Limit(2));
        var client = app.GetTestClient();
        var tom = TestAuth.CreateToken("tom", "acme", "requester");
        var maria = TestAuth.CreateToken("maria", "acme", "manager");

        var tomStatuses = await SendAsync(client, 3, () => Get(Me, tom, remoteIp: "10.1.1.1"));
        var mariaStatuses = await SendAsync(client, 2, () => Get(Me, maria, remoteIp: "10.1.1.1"));

        tomStatuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        mariaStatuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK]);
    }

    [Fact]
    public async Task The_same_user_id_in_another_tenant_is_a_separate_partition()
    {
        await using var app = await TestHost.StartAsync(Limit(1));
        var client = app.GetTestClient();
        var acme = TestAuth.CreateToken("u-1", "acme", "requester");
        var globex = TestAuth.CreateToken("u-1", "globex", "requester");

        (await SendAsync(client, 2, () => Get(Me, acme))).ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendAsync(client, 1, () => Get(Me, globex))).ShouldBe([HttpStatusCode.OK]);
    }

    [Fact]
    public async Task Anonymous_callers_share_their_client_ip_and_not_other_ips()
    {
        await using var app = await TestHost.StartAsync(Limit(2));
        var client = app.GetTestClient();

        (await SendAsync(client, 3, () => Get(Ping, remoteIp: "10.1.1.1")))
            .ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendAsync(client, 2, () => Get(Ping, remoteIp: "10.1.1.2"))).ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK]);
    }

    [Fact]
    public async Task Forwarded_client_ip_is_used_only_from_a_trusted_proxy()
    {
        var config = Limit(2);
        config["ForwardedHeaders:KnownProxies:0"] = "10.0.0.1";
        await using var app = await TestHost.StartAsync(config);
        var client = app.GetTestClient();

        // Trusted proxy: two different forwarded clients, two allowances.
        (await SendAsync(client, 3, () => Get(Ping, remoteIp: "10.0.0.1", forwardedFor: "203.0.113.5")))
            .ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        (await SendAsync(client, 2, () => Get(Ping, remoteIp: "10.0.0.1", forwardedFor: "203.0.113.6")))
            .ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK]);

        // Untrusted connection: the header is ignored, so changing it buys no new allowance.
        var statuses = new List<HttpStatusCode>();
        foreach (var ip in new[] { "203.0.113.7", "203.0.113.8", "203.0.113.9" })
        {
            statuses.Add((await client.SendAsync(Get(Ping, remoteIp: "10.0.0.9", forwardedFor: ip), Ct)).StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_when_no_proxy_is_configured()
    {
        await using var app = await TestHost.StartAsync(Limit(1));
        var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        foreach (var ip in new[] { "203.0.113.7", "203.0.113.8" })
        {
            statuses.Add((await client.SendAsync(Get(Ping, remoteIp: "10.0.0.1", forwardedFor: ip), Ct)).StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99")]
    [InlineData("ForwardedHeaders:ForwardLimit", "0")]
    public async Task Invalid_forwarded_headers_configuration_fails_at_startup(string key, string value)
    {
        await Should.ThrowAsync<OptionsValidationException>(() => TestHost.StartAsync(new() { [key] = value }));
    }
}
