using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// The gateway in front of the Notifications service (plan 04 Task 1a): its API and OpenAPI document like every service, and the live hub
/// at <c>/hubs/**</c>, proxied over WebSockets with the session's access token (ruling 10). The negotiate POST rides on the session cookie,
/// so it needs the anti-forgery header like every other state-changing call.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class LiveHubGatewayTests(KeycloakFixture keycloak, RedisFixture redis)
{
    private const string Negotiate = "/hubs/live/negotiate?negotiateVersion=1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync(GatewayHost gateway, string user = "tom@acme.test")
    {
        var browser = gateway.NewBrowser();
        using var driver = new LoginDriver(keycloak);
        await driver.LoginAsync(browser, user);
        return browser;
    }

    /// <summary>What the Angular app does: ask /bff/user, which hands out the XSRF token as a cookie.</summary>
    private static async Task<string> XsrfTokenAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/bff/user", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    private static JsonWebToken Bearer(SeenRequest seen)
    {
        var header = seen.Header("Authorization");
        header.ShouldNotBeNull();
        header.ShouldStartWith("Bearer ");
        return new JsonWebTokenHandler().ReadJsonWebToken(header["Bearer ".Length..]);
    }

    [Fact]
    public async Task Swagger_ui_lists_the_notifications_definition_and_the_gateway_serves_its_document_and_api()
    {
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis);
        using var browser = gateway.NewBrowser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/notifications/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/notifications/bell", Ct);

        config.ShouldContain("/openapi/notifications/v1.json");
        config.ShouldContain("\"notifications\"");
        document.StatusCode.ShouldBe(HttpStatusCode.OK);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the notifications API needs a session or key like every API");
        gateway.Stub.Seen.ShouldContain(s => s.Path == "/openapi/v1.json");
    }

    [Fact]
    public async Task Hub_without_a_session_is_401()
    {
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis);
        using var browser = gateway.NewBrowser();

        using var response = await browser.PostAsync(Negotiate, null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        gateway.Stub.Seen.ShouldBeEmpty("an unauthenticated hub call must not reach the service");
    }

    [Fact]
    public async Task Hub_negotiate_without_xsrf_header_is_refused()
    {
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis);
        var browser = await SignedInAsync(gateway);

        using var response = await browser.PostAsync(Negotiate, null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        gateway.Stub.Seen.ShouldNotContain(s => s.Path.StartsWith("/hubs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hub_negotiate_with_xsrf_header_is_proxied_with_the_sessions_token()
    {
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis);
        var browser = await SignedInAsync(gateway);
        var xsrf = await XsrfTokenAsync(browser);
        using var request = new HttpRequestMessage(HttpMethod.Post, Negotiate);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);

        using var response = await browser.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var seen = gateway.Stub.Last;
        seen.Path.ShouldBe("/hubs/live/negotiate");
        Bearer(seen).GetClaim("tenant").Value.ShouldBe("acme");
        seen.Header("Cookie").ShouldBeNull("the browser's cookies never reach a service");
    }

    [Fact]
    public async Task Hub_connects_over_websockets_through_gateway_with_session()
    {
        // A real port: YARP proxies a WebSocket only from a connection that can be upgraded.
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis, kestrel: true);
        var (browser, cookies) = gateway.NewBrowserWithCookies();
        using (browser)
        {
            using var driver = new LoginDriver(keycloak);
            await driver.LoginAsync(browser, "tom@acme.test");
        }

        var hub = new UriBuilder(gateway.Address) { Scheme = "wss", Path = "/hubs/live", Query = "id=connection-1" }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        // The browser sends its cookies with the upgrade request; it cannot send an Authorization header.
        socket.Options.SetRequestHeader("Cookie", cookies.CookieHeaderFor(gateway.Address));

        await socket.ConnectAsync(hub, Ct);
        var buffer = new byte[256];
        var received = await socket.ReceiveAsync(buffer, Ct);

        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("connected:/hubs/live");
        var upgrade = gateway.Stub.Seen.Last(s => s.Path == "/hubs/live");
        upgrade.Method.ShouldBe("GET");
        Bearer(upgrade).GetClaim("tenant").Value.ShouldBe("acme");
        upgrade.Header("Cookie").ShouldBeNull("the browser's cookies never reach a service");
    }

    [Fact]
    public async Task Hub_websocket_from_another_origin_is_refused_and_own_or_allowed_origins_connect()
    {
        // A page on another site can make the browser open a WebSocket with the session cookie (a GET: no anti-forgery header, and
        // SignalR can skip negotiate). Only the gateway's own origin and Hubs:AllowedOrigins get through.
        await using var gateway = await GatewayHost.StartAsync(
            keycloak, redis, new() { ["Hubs:AllowedOrigins:0"] = "https://app.test" }, kestrel: true);
        var (browser, cookies) = gateway.NewBrowserWithCookies();
        using (browser)
        {
            using var driver = new LoginDriver(keycloak);
            await driver.LoginAsync(browser, "tom@acme.test");
        }

        var cookieHeader = cookies.CookieHeaderFor(gateway.Address);
        var hub = new UriBuilder(gateway.Address) { Scheme = "wss", Path = "/hubs/live", Query = "id=connection-1" }.Uri;
        var own = gateway.Address.GetLeftPart(UriPartial.Authority);

        (await OpenAsync(hub, cookieHeader, "https://evil.test")).ShouldBe(HttpStatusCode.Forbidden);
        gateway.Stub.Seen.ShouldNotContain(s => s.Path == "/hubs/live", "a refused upgrade never reaches the service");
        (await OpenAsync(hub, cookieHeader, own)).ShouldBe(HttpStatusCode.SwitchingProtocols);
        (await OpenAsync(hub, cookieHeader, "https://app.test")).ShouldBe(HttpStatusCode.SwitchingProtocols);
    }

    /// <summary>Opens a WebSocket with the browser's cookie and <paramref name="origin"/>; returns the status of the upgrade answer.</summary>
    private static async Task<HttpStatusCode> OpenAsync(Uri hub, string cookieHeader, string origin)
    {
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Cookie", cookieHeader);
        socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            await socket.ConnectAsync(hub, Ct);
        }
        catch (WebSocketException)
        {
            return socket.HttpStatusCode;
        }

        var buffer = new byte[256];
        var received = await socket.ReceiveAsync(buffer, Ct);
        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("connected:/hubs/live");
        return socket.HttpStatusCode;
    }
}
