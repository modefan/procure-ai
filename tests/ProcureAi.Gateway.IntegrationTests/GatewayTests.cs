using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using ProcureAi.Gateway.Bff;
using StackExchange.Redis;

namespace ProcureAi.Gateway.IntegrationTests;

[Collection(GatewayCollection.Name)]
public sealed class GatewayTests(KeycloakFixture keycloak, RedisFixture redis) : IAsyncLifetime
{
    private const string Api = "/api/v1/requests";

    private GatewayHost _gateway = null!;
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _gateway = await GatewayHost.StartAsync(keycloak, redis);

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        await _gateway.DisposeAsync();
    }

    private HttpClient Browser()
    {
        var browser = _gateway.NewBrowser();
        _disposables.Add(browser);
        return browser;
    }

    private LoginDriver Driver()
    {
        var driver = new LoginDriver(keycloak);
        _disposables.Add(driver);
        return driver;
    }

    private async Task<HttpClient> SignedInAsync(string user = "tom@acme.test")
    {
        var browser = Browser();
        await Driver().LoginAsync(browser, user);
        return browser;
    }

    /// <summary>What the Angular app does: ask /bff/user, which hands out the XSRF token as a cookie.</summary>
    private static async Task<string> XsrfTokenAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/bff/user", Ct);
        await ExpectOkAsync(response);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(cookie["XSRF-TOKEN=".Length..].Split(';')[0]);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? xsrf = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        if (xsrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private static async Task ExpectOkAsync(HttpResponseMessage response) =>
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");

    private static JsonWebToken Bearer(SeenRequest seen)
    {
        var header = seen.Header("Authorization");
        header.ShouldNotBeNull();
        header.ShouldStartWith("Bearer ");
        return new JsonWebTokenHandler().ReadJsonWebToken(header["Bearer ".Length..]);
    }

    // ---- the six tests of the plan ----

    [Fact]
    public async Task Bff_user_is_401_when_anonymous()
    {
        using var response = await Browser().GetAsync("/bff/user", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Trace_id_passes_through_gateway()
    {
        var browser = Browser();

        // The gateway's own responses carry a trace ID, and so does a proxied one the service did not tag.
        using var own = await browser.GetAsync("/bff/user", Ct);
        using var proxied = await browser.SendAsync(
            Request(HttpMethod.Get, Api, headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);
        // A service's own trace ID and dev panel headers reach the browser untouched.
        using var tagged = await browser.SendAsync(
            Request(HttpMethod.Get, Api, headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme"), ("X-Stub-Trace-Id", "abc123abc123abc123abc123abc123ab"), ("X-Dev-Panel", "1")]), Ct);

        own.Headers.GetValues("X-Trace-Id").Single().Length.ShouldBe(32);
        proxied.Headers.GetValues("X-Trace-Id").Single().Length.ShouldBe(32);
        tagged.Headers.GetValues("X-Trace-Id").Single().ShouldBe("abc123abc123abc123abc123abc123ab");
        tagged.Headers.GetValues("X-Cache").Single().ShouldBe("hit; age-ms=7");
    }

    [Fact]
    public async Task Api_call_without_session_or_key_is_401()
    {
        using var response = await Browser().GetAsync(Api, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _gateway.Stub.Seen.ShouldBeEmpty("an unauthenticated call must not reach the service");
    }

    [Fact]
    public async Task Api_key_passes_through_to_service()
    {
        using var response = await Browser().SendAsync(
            Request(HttpMethod.Get, Api, headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        await ExpectOkAsync(response);
        var seen = _gateway.Stub.Last;
        seen.Header("X-Api-Key").ShouldBe("key-value");
        seen.Header("X-Tenant-Id").ShouldBe("acme");
        seen.Header("Authorization").ShouldBeNull("the gateway never adds a bearer token to an API key call");
    }

    [Fact]
    public async Task Logged_in_session_forwards_bearer_token()
    {
        var browser = await SignedInAsync();

        using var response = await browser.GetAsync(Api, Ct);

        await ExpectOkAsync(response);
        var token = Bearer(_gateway.Stub.Last);
        token.GetClaim("tenant").Value.ShouldBe("acme");
        token.Audiences.ShouldContain("procure-api");
        token.GetClaim("preferred_username").Value.ShouldBe("tom@acme.test");
    }

    [Fact]
    public async Task Mutating_call_without_xsrf_header_is_400()
    {
        var browser = await SignedInAsync();

        using var response = await browser.PostAsync(Api, JsonContent.Create(new { }), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _gateway.Stub.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_logs_are_accepted_anonymously_without_xsrf_header()
    {
        using var response = await Browser().PostAsync(
            "/api/client-logs", new StringContent("[{\"level\":\"warn\"}]", System.Text.Encoding.UTF8, "text/plain"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _gateway.Stub.Seen.ShouldBeEmpty("the stub endpoint is the gateway's own; nothing is proxied");
    }

    [Fact]
    public async Task Client_logs_are_accepted_for_a_signed_in_user_without_xsrf_header()
    {
        var browser = await SignedInAsync();

        using var response = await browser.PostAsync(
            "/api/client-logs", new StringContent("[]", System.Text.Encoding.UTF8, "text/plain"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Client_logs_over_the_size_limit_are_413()
    {
        using var response = await Browser().PostAsync(
            "/api/client-logs", new StringContent(new string('x', 70 * 1024), System.Text.Encoding.UTF8, "text/plain"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Swagger_ui_lists_requests_definition()
    {
        var browser = Browser();
        using var page = await browser.GetAsync("/swagger/index.html", Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The page loads its configuration (definitions, OAuth client) from this script.
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        initializer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        config.ShouldContain("/openapi/requests/v1.json", Case.Sensitive, await page.Content.ReadAsStringAsync(Ct));
        config.ShouldContain("\"requests\"");
        config.ShouldContain("procure-swagger");
        config.ShouldContain("usePkceWithAuthorizationCodeGrant");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_budgets_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/budgets/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/budgets", Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, "/api/v1/budgets/IT-200/FY2027", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/budgets/v1.json");
        config.ShouldContain("\"budgets\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the budgets API needs a session or key like every API");
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe("/api/v1/budgets/IT-200/FY2027");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_approvals_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/approvals/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/approvals/inbox", Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, "/api/v1/approvals/inbox", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/approvals/v1.json");
        config.ShouldContain("\"approvals\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the approvals API needs a session or key like every API");
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe("/api/v1/approvals/inbox");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_audit_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        var requestId = Guid.NewGuid();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/audit/v1.json", Ct);
        using var anonymous = await browser.GetAsync($"/api/v1/audit/requests/{requestId}/history", Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, $"/api/v1/audit/requests/{requestId}/history", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/audit/v1.json");
        config.ShouldContain("\"audit\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the audit API needs a session or key like every API");
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe($"/api/v1/audit/requests/{requestId}/history");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_suppliers_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/suppliers/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/suppliers", Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, "/api/v1/suppliers/contoso-hardware/items", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/suppliers/v1.json");
        config.ShouldContain("\"suppliers\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the suppliers API needs a session or key like every API");
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe("/api/v1/suppliers/contoso-hardware/items");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_purchasing_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/purchasing/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/purchasing/orders", Ct);
        // The supplier webhook is not exposed through the gateway yet (plan 07/08): without a session or key it is refused here.
        using var webhook = await browser.PostAsync("/api/v1/purchasing/webhooks/supplier", new StringContent("{}"), Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, "/api/v1/purchasing/orders", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/purchasing/v1.json");
        config.ShouldContain("\"purchasing\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the purchasing API needs a session or key like every API");
        webhook.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe("/api/v1/purchasing/orders");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_invoicing_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/invoicing/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/invoicing/invoices", Ct);
        // The supplier's invoice webhook is not exposed through the gateway yet (plan 07/08): without a session or key it is refused here.
        using var webhook = await browser.PostAsync("/api/v1/invoicing/webhooks/supplier", new StringContent("{}"), Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, "/api/v1/invoicing/invoices", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/invoicing/v1.json");
        config.ShouldContain("\"invoicing\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the invoicing API needs a session or key like every API");
        webhook.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe("/api/v1/invoicing/invoices");
    }

    [Fact]
    public async Task Swagger_ui_lists_the_assistant_definition_and_the_gateway_serves_its_document_and_api()
    {
        var browser = Browser();
        using var initializer = await browser.GetAsync("/swagger/index.js", Ct);
        var config = await initializer.Content.ReadAsStringAsync(Ct);
        using var document = await browser.GetAsync("/openapi/assistant/v1.json", Ct);
        using var anonymous = await browser.GetAsync("/api/v1/assistant/info", Ct);
        using var withKey = await browser.SendAsync(
            Request(HttpMethod.Get, "/api/v1/assistant/info", headers: [("X-Api-Key", "key-value"), ("X-Tenant-Id", "acme")]), Ct);

        config.ShouldContain("/openapi/assistant/v1.json");
        config.ShouldContain("\"assistant\"");
        await ExpectOkAsync(document);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the assistant API needs a session or key like every API");
        await ExpectOkAsync(withKey);
        _gateway.Stub.Last.Path.ShouldBe("/api/v1/assistant/info");
    }

    // ---- the rest of the contract ----

    [Fact]
    public async Task Openapi_document_is_proxied_anonymously_with_the_gateways_host()
    {
        using var response = await Browser().GetAsync("/openapi/requests/v1.json", Ct);

        await ExpectOkAsync(response);
        var seen = _gateway.Stub.Last;
        seen.Path.ShouldBe("/openapi/v1.json");
        // The service builds its "servers" entry from the Host header, so Swagger UI calls the gateway, not the service.
        seen.Header("Host").ShouldBe("localhost:5001");
    }

    [Fact]
    public async Task Bff_user_returns_identity_with_tenant_roles_and_permissions()
    {
        var browser = await SignedInAsync("maria@acme.test");

        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        user.GetProperty("userId").GetString().ShouldNotBeNullOrEmpty();
        user.GetProperty("name").GetString().ShouldNotBeNullOrEmpty();
        user.GetProperty("tenantId").GetString().ShouldBe("acme");
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("manager");
        user.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("requests.approve");
    }

    [Fact]
    public async Task Bff_user_has_tenant_name()
    {
        _gateway.Access.SetTenantName("acme", "Acme Corp");
        var browser = await SignedInAsync("maria@acme.test");

        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        user.GetProperty("tenantId").GetString().ShouldBe("acme");
        user.GetProperty("tenantName").GetString().ShouldBe("Acme Corp");
    }

    [Fact]
    public async Task Bff_user_has_no_tenant_name_when_access_is_down()
    {
        _gateway.Access.SetTenantName("acme", "Acme Corp");
        _gateway.Access.Down = true;
        var browser = await SignedInAsync("maria@acme.test");

        using var response = await browser.GetAsync("/bff/user", Ct);

        await ExpectOkAsync(response);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("tenantName").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Bff_user_permissions_come_from_access_and_are_cached_for_a_short_time()
    {
        _gateway.Access.SetMatrix("acme", new() { ["manager"] = ["requests.read", "budgets.read"] });
        var browser = await SignedInAsync("maria@acme.test");

        var first = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);
        var second = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        first.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldBe(["budgets.read", "requests.read"]);
        second.GetProperty("permissions").GetArrayLength().ShouldBe(2);
        _gateway.Access.EffectiveCalls.ShouldBe(1);
        _gateway.Access.Calls.Single(c => c.Path.EndsWith("/effective", StringComparison.Ordinal)).Tenant.ShouldBe("acme");
    }

    [Fact]
    public async Task Bff_user_has_no_permissions_but_still_answers_when_access_is_down()
    {
        _gateway.Access.Down = true;
        var browser = await SignedInAsync("maria@acme.test");

        using var response = await browser.GetAsync("/bff/user", Ct);

        await ExpectOkAsync(response);
        var user = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        user.GetProperty("userId").GetString().ShouldNotBeNullOrEmpty();
        user.GetProperty("permissions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Session_cookie_is_host_prefixed_http_only_secure_and_strict()
    {
        var browser = Browser();
        var driver = Driver();
        var authorize = await LoginDriver.StartAsync(browser, "/bff/login");
        var (action, _) = await driver.OpenLoginPageAsync(authorize);
        var callback = await driver.SubmitCredentialsAsync(action!, "tom@acme.test", keycloak.DemoPassword);

        using var response = await browser.GetAsync(callback, Ct);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-procure=", StringComparison.Ordinal)).ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("secure");
        cookie.ShouldContain("samesite=strict");
        cookie.ShouldContain("path=/");
        cookie.ShouldNotContain("domain=");
        // Only a session key is in the cookie: the tokens stay in Redis.
        cookie.ShouldNotContain("eyj");
    }

    [Fact]
    public async Task Return_url_must_be_local()
    {
        var browser = Browser();

        var local = await Driver().LoginAsync(browser, "tom@acme.test", "/bff/login?returnUrl=%2Forders%2F7");
        local.OriginalString.ShouldBe("/orders/7");

        var other = Browser();
        var external = await Driver().LoginAsync(other, "tom@acme.test", "/bff/login?returnUrl=https%3A%2F%2Fevil.example%2F");
        external.OriginalString.ShouldBe("/");
    }

    [Fact]
    public async Task Session_state_is_kept_in_redis_not_in_the_browser()
    {
        await SignedInAsync();

        var keys = SessionKeys();
        keys.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Incoming_authorization_header_and_cookies_are_not_forwarded_on_cookie_calls()
    {
        var browser = await SignedInAsync();

        using var response = await browser.SendAsync(
            Request(HttpMethod.Get, Api, headers: [("Authorization", "Basic forged"), ("Cookie", "other=1")]), Ct);

        await ExpectOkAsync(response);
        var seen = _gateway.Stub.Last;
        seen.Header("Authorization").ShouldNotBe("Basic forged");
        Bearer(seen).GetClaim("tenant").Value.ShouldBe("acme");
        seen.Header("Cookie").ShouldBeNull("the session cookie must never reach a service");
    }

    [Fact]
    public async Task Api_key_call_from_a_signed_in_browser_is_forwarded_unchanged_without_a_bearer()
    {
        var browser = await SignedInAsync();

        using var response = await browser.SendAsync(Request(HttpMethod.Get, Api, headers: ("X-Api-Key", "key-value")), Ct);

        await ExpectOkAsync(response);
        var seen = _gateway.Stub.Last;
        seen.Header("X-Api-Key").ShouldBe("key-value");
        seen.Header("Authorization").ShouldBeNull();
        seen.Header("Cookie").ShouldBeNull();
    }

    [Fact]
    public async Task Bearer_token_without_a_session_passes_through_for_the_service_to_validate()
    {
        // Swagger UI signs in itself (procure-swagger) and sends its own token.
        using var response = await Browser().SendAsync(Request(HttpMethod.Get, Api, headers: ("Authorization", "Bearer swagger-token")), Ct);

        await ExpectOkAsync(response);
        _gateway.Stub.Last.Header("Authorization").ShouldBe("Bearer swagger-token");
    }

    [Fact]
    public async Task Explicit_bearer_header_wins_over_the_session_and_needs_no_xsrf_token()
    {
        // Swagger UI on the gateway origin sends its own OAuth2 token; the session token is not attached.
        var browser = await SignedInAsync();

        using var response = await browser.SendAsync(Request(HttpMethod.Post, Api, headers: ("Authorization", "Bearer swagger-token")), Ct);

        await ExpectOkAsync(response);
        _gateway.Stub.Last.Header("Authorization").ShouldBe("Bearer swagger-token");
        _gateway.Stub.Last.Header("Cookie").ShouldBeNull();
    }

    [Theory]
    [InlineData("/bff/logout")]
    [InlineData("/bff/logout-all")]
    public async Task Api_key_header_does_not_skip_xsrf_on_bff_endpoints(string path)
    {
        var browser = await SignedInAsync();

        using var response = await browser.SendAsync(Request(HttpMethod.Post, path, headers: ("X-Api-Key", "key-value")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_session_revoked_while_a_request_was_in_flight_is_not_renewed_back_to_life()
    {
        var browser = await SignedInAsync();
        var id = SessionIds().Single();
        var store = _gateway.App.Services.GetRequiredService<RedisTicketStore>();
        var ticket = (await store.RetrieveAsync(id, Ct))!;
        // The renewal of a request that read the ticket before logout-all removed it.
        (await store.RevokeAllForUserAsync(ticket.Principal.FindFirst("sub")!.Value, Ct)).ShouldBe(1);

        await store.RenewAsync(id, ticket, Ct);

        SessionKeys().ShouldBeEmpty();
        (await store.RetrieveAsync(id, Ct)).ShouldBeNull();
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenant_and_roles_are_taken_from_the_new_token_when_it_is_refreshed()
    {
        var browser = await SignedInAsync();
        var id = SessionIds().Single();
        var store = _gateway.App.Services.GetRequiredService<RedisTicketStore>();
        var ticket = (await store.RetrieveAsync(id, Ct))!;
        var identity = (System.Security.Claims.ClaimsIdentity)ticket.Principal.Identity!;
        foreach (var stale in identity.FindAll("tenant").Concat(identity.FindAll("roles")).ToList())
        {
            identity.RemoveClaim(stale);
        }

        identity.AddClaim(new System.Security.Claims.Claim("tenant", "stale-tenant"));
        await store.RenewAsync(id, ticket, Ct);
        (await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct)).GetProperty("tenantId").GetString().ShouldBe("stale-tenant");

        _gateway.Clock.Advance(TimeSpan.FromHours(1));
        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        user.GetProperty("tenantId").GetString().ShouldBe("acme");
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("requester");
    }

    [Fact]
    public async Task Mutating_call_with_matching_xsrf_header_is_proxied()
    {
        var browser = await SignedInAsync();
        var xsrf = await XsrfTokenAsync(browser);

        using var response = await browser.SendAsync(Request(HttpMethod.Post, Api, xsrf), Ct);

        await ExpectOkAsync(response);
        _gateway.Stub.Last.Method.ShouldBe("POST");
        Bearer(_gateway.Stub.Last).GetClaim("tenant").Value.ShouldBe("acme");
    }

    [Fact]
    public async Task Mutating_call_with_a_wrong_xsrf_header_is_400()
    {
        var browser = await SignedInAsync();
        await XsrfTokenAsync(browser);

        using var response = await browser.SendAsync(Request(HttpMethod.Delete, Api + "/1", "not-the-token"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _gateway.Stub.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mutating_calls_with_an_api_key_need_no_xsrf_header()
    {
        using var response = await Browser().SendAsync(Request(HttpMethod.Post, Api, headers: ("X-Api-Key", "key-value")), Ct);

        await ExpectOkAsync(response);
    }

    [Fact]
    public async Task Everything_else_goes_to_the_web_app_anonymously_without_a_token()
    {
        var browser = await SignedInAsync();

        using var page = await browser.GetAsync("/requests/new", Ct);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        _gateway.Stub.Last.Path.ShouldBe("/requests/new");
        _gateway.Stub.Last.Header("Authorization").ShouldBeNull("the web app gets no access token");

        using var anonymous = await Browser().GetAsync("/", Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_api_paths_are_404_and_not_the_web_app()
    {
        using var response = await Browser().GetAsync("/api/v1/nonexistent", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _gateway.Stub.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task Register_opens_keycloaks_registration_form_not_the_login_form()
    {
        // The request goes to Keycloak as a pushed authorization request (the gateway pushes its parameters,
        // prompt=create among them, over the back channel), so the proof is the page Keycloak shows.
        var driver = Driver();
        var authorize = await LoginDriver.StartAsync(Browser(), "/bff/register?returnUrl=%2F");
        authorize.AbsoluteUri.ShouldStartWith(keycloak.BaseUrl);

        var (loginAction, redirect) = await driver.OpenLoginPageAsync(authorize);

        redirect.ShouldBeNull();
        loginAction.ShouldBeNull();
        driver.IsRegistrationPage.ShouldBeTrue();
    }

    [Fact]
    public async Task Newly_registered_user_has_no_tenant_and_no_permissions()
    {
        var browser = Browser();
        var driver = Driver();
        var authorize = await LoginDriver.StartAsync(browser, "/bff/register");
        var (loginAction, _) = await driver.OpenLoginPageAsync(authorize);
        // prompt=create opens the registration form (what a "Register" button leads to), not the login form.
        loginAction.ShouldBeNull();
        var email = $"new-{Guid.NewGuid():N}@registered.test";
        var callback = await driver.RegisterAsync(email, keycloak.DemoPassword);
        using var done = await browser.GetAsync(callback, Ct);
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        user.GetProperty("tenantId").ValueKind.ShouldBe(JsonValueKind.Null);
        user.GetProperty("permissions").GetArrayLength().ShouldBe(0);
        user.GetProperty("userId").GetString().ShouldNotBeNullOrEmpty();
    }

    /// <summary>Registers through Keycloak's own form and returns a browser holding the new session.</summary>
    private async Task<HttpClient> RegisteredBrowserAsync()
    {
        var browser = Browser();
        var driver = Driver();
        var authorize = await LoginDriver.StartAsync(browser, "/bff/register");
        await driver.OpenLoginPageAsync(authorize);
        var callback = await driver.RegisterAsync($"new-{Guid.NewGuid():N}@registered.test", keycloak.DemoPassword);
        using var done = await browser.GetAsync(callback, Ct);
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return browser;
    }

    [Fact]
    public async Task New_user_after_first_bff_user_call_has_tenant_demo_and_requester_permissions()
    {
        // A fresh organization plays "demo", so no test depends on another's members.
        var tenant = $"d{Guid.NewGuid():N}"[..12];
        var organizationId = await keycloak.CreateOrganizationAsync(tenant);
        await _gateway.DisposeAsync();
        _gateway = await GatewayHost.StartAsync(keycloak, redis, new() { ["Registration:DemoTenantId"] = tenant });
        _gateway.Access.SetMatrix(tenant, new() { ["requester"] = ["requests.create", "requests.read.own"] });
        // What the real Access does in Keycloak.
        _gateway.Access.OnRegister = async registration =>
        {
            await keycloak.AddOrganizationMemberAsync(organizationId, registration.UserId);
            await keycloak.AddRealmRoleAsync(registration.UserId, "requester");
        };
        var browser = await RegisteredBrowserAsync();

        var user = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);

        user.GetProperty("tenantId").GetString().ShouldBe(tenant);
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("requester");
        user.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldBe(["requests.create", "requests.read.own"]);
        var registration = _gateway.Access.Registrations.Single();
        registration.Tenant.ShouldBe(tenant);
        registration.UserId.ShouldBe(user.GetProperty("userId").GetString());
        // Access binds the registration to the user the gateway acts for (final review I-5).
        registration.OnBehalfOf.ShouldBe(registration.UserId);
        registration.IdempotencyKey.ShouldNotBeNullOrEmpty();

        // The second call finds the tenant in the session already: no second registration.
        var again = await browser.GetFromJsonAsync<JsonElement>("/bff/user", Ct);
        again.GetProperty("tenantId").GetString().ShouldBe(tenant);
        _gateway.Access.Registrations.Count.ShouldBe(1);
        SessionKeys().Length.ShouldBe(1);
    }

    [Fact]
    public async Task Failed_registration_leaves_the_new_user_without_tenant_and_the_answer_is_not_500()
    {
        _gateway.Access.OnRegister = _ => throw new InvalidOperationException("Keycloak is down");
        var browser = await RegisteredBrowserAsync();

        using var response = await browser.GetAsync("/bff/user", Ct);

        await ExpectOkAsync(response);
        var user = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        user.GetProperty("tenantId").ValueKind.ShouldBe(JsonValueKind.Null);
        user.GetProperty("permissions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task User_with_a_tenant_is_never_registered()
    {
        var browser = await SignedInAsync();

        await ExpectOkAsync(await browser.GetAsync("/bff/user", Ct));

        _gateway.Access.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_ends_the_keycloak_session()
    {
        var browser = Browser();
        var driver = Driver();
        await driver.LoginAsync(browser, "tom@acme.test");
        var xsrf = await XsrfTokenAsync(browser);
        SessionKeys().Length.ShouldBe(1);
        // While Keycloak's SSO session lives, signing in again skips the form.
        (await driver.OpenLoginPageAsync(await LoginDriver.StartAsync(Browser(), "/bff/login"))).Redirect.ShouldNotBeNull();

        using var response = await browser.SendAsync(Request(HttpMethod.Post, "/bff/logout", xsrf), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionKeys().ShouldBeEmpty();
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync(Api, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await driver.OpenLoginPageAsync(await LoginDriver.StartAsync(Browser(), "/bff/login"))).FormAction.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("/bff/logout")]
    [InlineData("/bff/logout-all")]
    public async Task Logout_sends_clear_site_data_cache(string path)
    {
        var browser = await SignedInAsync();
        var xsrf = await XsrfTokenAsync(browser);

        using var response = await browser.SendAsync(Request(HttpMethod.Post, path, xsrf), Ct);

        response.IsSuccessStatusCode.ShouldBeTrue();
        // The browser drops its HTTP cache, so the next user of this browser cannot see this user's cached API responses.
        response.Headers.GetValues("Clear-Site-Data").Single().ShouldBe("\"cache\"");
    }

    [Fact]
    public async Task Login_completion_sends_clear_site_data_cache()
    {
        var driver = Driver();

        await driver.LoginAsync(Browser(), "tom@acme.test");

        // A new session cookie starts with an empty HTTP cache, so whoever signed in before in this browser leaves nothing
        // behind (version ETags are the same in every tenant; the cache must not answer 304 for the next user).
        driver.CallbackHeaders.ShouldNotBeNull();
        driver.CallbackHeaders["Clear-Site-Data"].ShouldBe("\"cache\"");
    }

    [Fact]
    public async Task Logout_without_xsrf_header_is_400_and_keeps_the_session()
    {
        var browser = await SignedInAsync();

        using var response = await browser.PostAsync("/bff/logout", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_all_revokes_every_session_of_the_user_and_leaves_others_alone()
    {
        var first = await SignedInAsync("tom@acme.test");
        var second = await SignedInAsync("tom@acme.test");
        var other = await SignedInAsync("maria@acme.test");
        SessionKeys().Length.ShouldBe(3);
        var xsrf = await XsrfTokenAsync(first);

        using var response = await first.SendAsync(Request(HttpMethod.Post, "/bff/logout-all", xsrf), Ct);

        await ExpectOkAsync(response);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("revoked").GetInt32().ShouldBe(2);
        (await first.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await second.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await other.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        SessionKeys().Length.ShouldBe(1);
    }

    [Fact]
    public async Task Access_token_is_refreshed_server_side_before_it_expires()
    {
        var browser = await SignedInAsync();
        (await browser.GetAsync(Api, Ct)).EnsureSuccessStatusCode();
        var before = Bearer(_gateway.Stub.Last);

        // Past the token's lifetime: the next call refreshes with the stored refresh token first.
        _gateway.Clock.Advance(TimeSpan.FromHours(1));
        (await browser.GetAsync(Api, Ct)).EnsureSuccessStatusCode();
        var after = Bearer(_gateway.Stub.Last);

        after.EncodedToken.ShouldNotBe(before.EncodedToken);
        after.GetClaim("tenant").Value.ShouldBe("acme");
        after.ValidTo.ShouldBeGreaterThanOrEqualTo(before.ValidTo);
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        SessionKeys().Length.ShouldBe(1, "the stored session is updated, not duplicated");
    }

    [Fact]
    public async Task Failed_refresh_ends_the_session()
    {
        var browser = await SignedInAsync();
        var key = SessionIds().Single();
        var store = _gateway.App.Services.GetRequiredService<RedisTicketStore>();
        var ticket = (await store.RetrieveAsync(key, Ct))!;
        ticket.Properties.UpdateTokenValue("refresh_token", "no-longer-valid");
        await store.RenewAsync(key, ticket, Ct);

        _gateway.Clock.Advance(TimeSpan.FromHours(1));
        using var api = await browser.GetAsync(Api, Ct);

        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync("/bff/user", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        SessionKeys().ShouldBeEmpty("the session is removed from Redis");
    }

    [Fact]
    public async Task Gateway_limits_requests_by_client_ip()
    {
        await using var limited = await GatewayHost.StartAsync(keycloak, redis, new()
        {
            ["RateLimiting:PermitLimit"] = "2",
            ["RateLimiting:WindowSeconds"] = "60",
        });
        using var browser = limited.NewBrowser();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await browser.GetAsync("/bff/user", Ct)).StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests]);
    }

    [Theory]
    [InlineData("Oidc:ClientSecret", "")]
    [InlineData("Oidc:Authority", "")]
    [InlineData("Session:RefreshSkewSeconds", "0")]
    [InlineData("Swagger:Definitions:0:Url", "no-leading-slash")]
    [InlineData("Hubs:AllowedOrigins:0", "https://app.test/path")]
    [InlineData("Hubs:AllowedOrigins:0", "not-an-origin")]
    public async Task Invalid_configuration_fails_at_startup(string key, string value)
    {
        await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            () => GatewayHost.StartAsync(keycloak, redis, new() { [key] = value }));
    }

    private string[] SessionIds()
    {
        var prefix = $"{_gateway.KeyPrefix}session:";
        return [.. SessionKeys().Select(k => ((string)k!)[prefix.Length..])];
    }

    private RedisKey[] SessionKeys()
    {
        using var connection = ConnectionMultiplexer.Connect(redis.ConnectionString + ",allowAdmin=true");
        var server = connection.GetServer(connection.GetEndPoints().First());
        return [.. server.Keys(pattern: $"{_gateway.KeyPrefix}session:*")];
    }
}
