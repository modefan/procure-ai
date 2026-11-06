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

    [Fact]
    public async Task Hub_websocket_over_http2_connects_through_gateway_with_session()
    {
        // Chrome opens a WebSocket over its existing HTTP/2 connection to the gateway (RFC 8441 extended CONNECT; Kestrel offers it) when
        // nothing in between speaks HTTP/1.1, as under the AppHost. The CONNECT carries the cookie and the Origin but no anti-forgery
        // header, like the HTTP/1.1 GET upgrade; refusing it made SignalR fall back to long polling (plan 07 carry-over 22).
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis, kestrel: true);
        var (browser, cookies) = gateway.NewBrowserWithCookies();
        using (browser)
        {
            using var driver = new LoginDriver(keycloak);
            await driver.LoginAsync(browser, "tom@acme.test");
        }

        var hub = new UriBuilder(gateway.Address) { Scheme = "wss", Path = "/hubs/live", Query = "id=connection-1" }.Uri;
        using var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } };
        using var invoker = new HttpMessageInvoker(handler);
        using var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version20;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Cookie", cookies.CookieHeaderFor(gateway.Address));
        socket.Options.SetRequestHeader("Origin", gateway.Address.GetLeftPart(UriPartial.Authority));

        try
        {
            await socket.ConnectAsync(hub, invoker, Ct);
        }
        catch (WebSocketException error)
        {
            throw new ShouldAssertException($"The HTTP/2 WebSocket was refused with {(int)socket.HttpStatusCode}.", error);
        }

        socket.HttpStatusCode.ShouldBe(HttpStatusCode.OK, "an HTTP/2 WebSocket is accepted with 200, not 101");
        var buffer = new byte[256];
        var received = await socket.ReceiveAsync(buffer, Ct);
        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("connected:/hubs/live");
        var upgrade = gateway.Stub.Seen.Last(s => s.Path == "/hubs/live");
        Bearer(upgrade).GetClaim("tenant").Value.ShouldBe("acme");
        upgrade.Header("Cookie").ShouldBeNull("the browser's cookies never reach a service");
    }

    [Fact]
    public async Task Hub_websocket_over_http2_from_another_origin_or_without_a_session_is_refused()
    {
        // The anti-forgery check skips hub WebSockets because the Origin check covers them: over HTTP/2 (extended CONNECT) a foreign origin
        // must still be refused before anything reaches Notifications, and a socket without the session cookie must not get in either.
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis, kestrel: true);
        var (browser, cookies) = gateway.NewBrowserWithCookies();
        using (browser)
        {
            using var driver = new LoginDriver(keycloak);
            await driver.LoginAsync(browser, "tom@acme.test");
        }

        var hub = new UriBuilder(gateway.Address) { Scheme = "wss", Path = "/hubs/live", Query = "id=connection-1" }.Uri;
        var own = gateway.Address.GetLeftPart(UriPartial.Authority);

        (await OpenOverHttp2Async(hub, cookies.CookieHeaderFor(gateway.Address), "https://evil.test")).ShouldBe(HttpStatusCode.Forbidden);
        (await OpenOverHttp2Async(hub, cookies.CookieHeaderFor(gateway.Address), "null")).ShouldBe(HttpStatusCode.Forbidden);
        (await OpenOverHttp2Async(hub, cookieHeader: null, own)).ShouldBe(HttpStatusCode.Unauthorized);
        gateway.Stub.Seen.ShouldNotContain(s => s.Path.StartsWith("/hubs", StringComparison.Ordinal), "a refused socket never reaches the service");
    }

    [Fact]
    public async Task Hub_long_polling_send_without_xsrf_header_is_refused_also_with_an_upgrade_header()
    {
        // Only a real WebSocket (GET + Upgrade, or an extended CONNECT) skips the anti-forgery check; a long-polling send (POST
        // /hubs/live?id=...) rides on the cookie and needs the header, even if it carries an Upgrade header.
        await using var gateway = await GatewayHost.StartAsync(keycloak, redis);
        var browser = await SignedInAsync(gateway);

        using var plain = await browser.PostAsync("/hubs/live?id=connection-1", new StringContent("{}"), Ct);
        using var withUpgrade = new HttpRequestMessage(HttpMethod.Post, "/hubs/live?id=connection-1") { Content = new StringContent("{}") };
        withUpgrade.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        withUpgrade.Headers.TryAddWithoutValidation("Origin", gateway.Address.GetLeftPart(UriPartial.Authority));
        using var upgraded = await browser.SendAsync(withUpgrade, Ct);

        plain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        upgraded.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        gateway.Stub.Seen.ShouldNotContain(s => s.Path.StartsWith("/hubs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hub_websocket_behind_a_proxy_that_forwards_wss_is_from_the_own_origin()
    {
        // Traefik forwards a WebSocket upgrade with X-Forwarded-Proto: wss, so the request's scheme is wss while the page's origin is
        // https://<host>. The page is the gateway's own origin all the same, without listing it in Hubs:AllowedOrigins.
        await using var gateway = await GatewayHost.StartAsync(
            keycloak, redis, new() { ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1" }, kestrel: true);
        var (browser, cookies) = gateway.NewBrowserWithCookies();
        using (browser)
        {
            using var driver = new LoginDriver(keycloak);
            await driver.LoginAsync(browser, "tom@acme.test");
        }

        var cookieHeader = cookies.CookieHeaderFor(gateway.Address);
        var hub = new UriBuilder(gateway.Address) { Scheme = "wss", Path = "/hubs/live", Query = "id=connection-1" }.Uri;
        var own = gateway.Address.GetLeftPart(UriPartial.Authority);
        var asWebSocket = new Dictionary<string, string> { ["X-Forwarded-Proto"] = "wss" };

        (await OpenAsync(hub, cookieHeader, own, asWebSocket)).ShouldBe(HttpStatusCode.SwitchingProtocols);
        (await OpenAsync(hub, cookieHeader, "https://evil.test", asWebSocket)).ShouldBe(HttpStatusCode.Forbidden);
        (await OpenAsync(hub, cookieHeader, own.Replace("https://", "wss://", StringComparison.Ordinal), asWebSocket))
            .ShouldBe(HttpStatusCode.Forbidden, "a browser page never has a wss origin");
    }

    /// <summary>Opens a WebSocket over HTTP/2 (extended CONNECT) as Chrome does straight to Kestrel; returns the status of the answer.</summary>
    private static async Task<HttpStatusCode> OpenOverHttp2Async(Uri hub, string? cookieHeader, string origin)
    {
        using var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } };
        using var invoker = new HttpMessageInvoker(handler);
        using var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version20;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.CollectHttpResponseDetails = true;
        if (cookieHeader is not null)
        {
            socket.Options.SetRequestHeader("Cookie", cookieHeader);
        }

        socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            await socket.ConnectAsync(hub, invoker, Ct);
        }
        catch (WebSocketException)
        {
            return socket.HttpStatusCode;
        }

        return socket.HttpStatusCode;
    }

    /// <summary>Opens a WebSocket with the browser's cookie and <paramref name="origin"/>; returns the status of the upgrade answer.</summary>
    private static async Task<HttpStatusCode> OpenAsync(Uri hub, string cookieHeader, string origin, Dictionary<string, string>? headers = null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Cookie", cookieHeader);
        socket.Options.SetRequestHeader("Origin", origin);
        foreach (var (name, value) in headers ?? [])
        {
            socket.Options.SetRequestHeader(name, value);
        }
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
