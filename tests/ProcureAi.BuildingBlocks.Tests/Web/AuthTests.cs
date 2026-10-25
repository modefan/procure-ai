using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

[Collection(GlobalStateCollection.Name)]
public class AuthTests
{
    private const string Me = "/api/v1/auth-test/me";
    private const string TenantEndpoint = "/api/v1/auth-test/tenant";
    private const string Approve = "/api/v1/auth-test/approve";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Request(HttpMethod method, string url, string? apiKey = null,
        string? bearer = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return request;
    }

    [Fact]
    public async Task Valid_api_key_authenticates_as_claude_admin()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().SendAsync(
            Request(HttpMethod.Get, Me, TestAuth.ApiKey, headers: ("X-Tenant-Id", "acme")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("userId").GetString().ShouldBe("claude-admin");
        body.GetProperty("tenantId").GetString().ShouldBe("acme");
        body.GetProperty("isAdminApiKey").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Api_key_exposes_calling_service_and_on_behalf_of_user()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey,
            headers: [("X-Tenant-Id", "acme"), ("X-Calling-Service", "requests"), ("X-On-Behalf-Of-User", "user-9")]), Ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("callingService").GetString().ShouldBe("requests");
        body.GetProperty("onBehalfOfUser").GetString().ShouldBe("user-9");
    }

    [Theory]
    [InlineData("not-the-key")]
    [InlineData("   x   ")]
    public async Task Wrong_api_key_is_401_and_not_anonymous(string key)
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-1", "acme", "admin");

        // A valid Bearer token next to a wrong key must not rescue the request: no fallback to Bearer.
        var response = await app.GetTestClient().SendAsync(
            Request(HttpMethod.Get, Me, key, bearer: token, headers: ("X-Tenant-Id", "acme")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Key_with_a_different_hash_in_config_is_rejected()
    {
        await using var app = await TestHost.StartAsync(new()
        {
            ["Auth:AdminApiKeySha256"] = TestAuth.Hash("some-other-key"),
        });

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_is_rejected_when_no_key_is_configured()
    {
        await using var app = await TestHost.StartAsync(new() { ["Auth:AdminApiKeySha256"] = "" });

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("abcd")]
    public async Task Invalid_api_key_hash_fails_at_startup(string hash)
    {
        await Should.ThrowAsync<OptionsValidationException>(
            () => TestHost.StartAsync(new() { ["Auth:AdminApiKeySha256"] = hash }));
    }

    [Fact]
    public async Task Missing_authority_fails_at_startup()
    {
        await Should.ThrowAsync<OptionsValidationException>(
            () => TestHost.StartAsync(new() { ["Auth:Authority"] = "" }));
    }

    [Fact]
    public async Task Api_key_without_tenant_on_tenant_endpoint_is_400()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, TenantEndpoint, TestAuth.ApiKey), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("X-Tenant-Id header is required");
    }

    [Fact]
    public async Task Api_key_with_tenant_passes_tenant_endpoint()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().SendAsync(
            Request(HttpMethod.Get, TenantEndpoint, TestAuth.ApiKey, headers: ("X-Tenant-Id", "globex")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_key_value_is_never_logged()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(sink: sink);
        var client = app.GetTestClient();

        await client.SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey, headers: ("X-Tenant-Id", "acme")), Ct);
        await client.SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey + "x"), Ct);

        var rendered = string.Join('\n', sink.Events.Select(e => e.RenderMessage() + string.Join(',', e.Properties.Values)));
        rendered.ShouldContain("claude-admin");
        rendered.ShouldNotContain(TestAuth.ApiKey);
        rendered.ShouldNotContain(TestAuth.ApiKeySha256);
    }

    [Fact]
    public async Task Api_key_calls_are_audited_as_claude_admin_with_the_tenant()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(sink: sink);

        await app.GetTestClient().SendAsync(
            Request(HttpMethod.Get, Me, TestAuth.ApiKey, headers: ("X-Tenant-Id", "acme")), Ct);

        var audit = sink.Events.Single(e => e.MessageTemplate.Text.StartsWith("Admin API key call", StringComparison.Ordinal));
        audit.Properties["UserId"].ToString().ShouldBe("\"claude-admin\"");
        audit.Properties["TenantId"].ToString().ShouldBe("\"acme\"");
    }

    [Fact]
    public async Task Log_events_of_api_key_calls_carry_claude_admin_and_the_header_tenant()
    {
        var sink = new CapturingSink();
        await using var app = await TestHost.StartAsync(sink: sink);

        await app.GetTestClient().SendAsync(Request(HttpMethod.Get, "/api/v1/auth-test/log", TestAuth.ApiKey,
            headers: ("X-Tenant-Id", "acme")), Ct);

        var logged = sink.Events.Single(e => e.RenderMessage() == "auth-test-event");
        logged.Properties["UserId"].ToString().ShouldBe("\"claude-admin\"");
        logged.Properties["TenantId"].ToString().ShouldBe("\"acme\"");
    }

    [Fact]
    public async Task Jwt_requester_cannot_approve()
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-1", "acme", "requester");

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Post, Approve, bearer: token), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Jwt_manager_can_approve()
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-2", "acme", "manager");

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Post, Approve, bearer: token), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Jwt_claims_keep_their_names_and_roles_may_be_several()
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-3", "globex", "requester", "manager");

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, Me, bearer: token), Ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("userId").GetString().ShouldBe("user-3");
        body.GetProperty("tenantId").GetString().ShouldBe("globex");
        body.GetProperty("isAdminApiKey").GetBoolean().ShouldBeFalse();
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["manager", "requester"]);
    }

    [Fact]
    public async Task Jwt_cannot_forge_the_api_key_identity()
    {
        await using var app = await TestHost.StartAsync();
        var forged = new Dictionary<string, object>
        {
            ["auth_method"] = "api_key",
            ["calling_service"] = "evil",
            ["on_behalf_of"] = "someone",
        };
        var token = TestAuth.CreateToken("user-1", "acme", forged, "requester");

        var approve = await app.GetTestClient().SendAsync(Request(HttpMethod.Post, Approve, bearer: token), Ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var me = await app.GetTestClient().SendAsync(
            Request(HttpMethod.Get, Me, bearer: token, headers: ("X-Tenant-Id", "globex")), Ct);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("isAdminApiKey").GetBoolean().ShouldBeFalse();
        body.GetProperty("tenantId").GetString().ShouldBe("acme");
        body.GetProperty("callingService").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("onBehalfOfUser").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private const string InternalClient = "procure-internal";

    private static readonly (string Name, string Value)[] ForgedHeaders =
    [
        ("X-Calling-Service", "approvals-api"), ("X-On-Behalf-Of-User", "tom"), ("X-Tenant-Id", "globex"),
    ];

    private static async Task<JsonElement> MeAsync(
        string? internalClientId, string? azp, params (string Name, string Value)[] headers)
    {
        var config = internalClientId is null ? null : new Dictionary<string, string?> { ["Auth:InternalClientId"] = internalClientId };
        await using var app = await TestHost.StartAsync(config);
        var claims = azp is null ? null : new Dictionary<string, object> { ["azp"] = azp };
        var token = TestAuth.CreateToken("svc-account", "acme", claims, "requester");
        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, Me, bearer: token, headers: headers), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static void ShouldReadNothing(JsonElement body)
    {
        body.GetProperty("isServiceAccount").GetBoolean().ShouldBeFalse();
        body.GetProperty("callingService").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("onBehalfOfUser").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("tenantId").GetString().ShouldBe("acme");
    }

    [Fact]
    public async Task Normal_user_jwt_cannot_set_calling_service_on_behalf_of_or_tenant_by_header()
    {
        // Even with an internal client configured: this token's azp is not it.
        ShouldReadNothing(await MeAsync(InternalClient, "procure-web", ForgedHeaders));
        ShouldReadNothing(await MeAsync(InternalClient, null, ForgedHeaders));
    }

    [Fact]
    public async Task Service_account_token_reads_calling_service_on_behalf_of_and_tenant_headers()
    {
        var body = await MeAsync(InternalClient, InternalClient, ForgedHeaders);

        body.GetProperty("isServiceAccount").GetBoolean().ShouldBeTrue();
        body.GetProperty("isAdminApiKey").GetBoolean().ShouldBeFalse();
        body.GetProperty("callingService").GetString().ShouldBe("approvals-api");
        body.GetProperty("onBehalfOfUser").GetString().ShouldBe("tom");
        body.GetProperty("tenantId").GetString().ShouldBe("globex");
    }

    [Fact]
    public async Task Service_account_token_passes_internal_policy_user_token_does_not()
    {
        const string internalEndpoint = "/api/v1/auth-test/internal";
        await using var app = await TestHost.StartAsync(new() { ["Auth:InternalClientId"] = InternalClient });
        var client = app.GetTestClient();

        var service = await client.SendAsync(
            Request(HttpMethod.Get, internalEndpoint, bearer: TestAuth.CreateServiceToken(InternalClient)), Ct);
        var otherClient = await client.SendAsync(
            Request(HttpMethod.Get, internalEndpoint, bearer: TestAuth.CreateServiceToken("some-other-client")), Ct);
        var user = await client.SendAsync(
            Request(HttpMethod.Get, internalEndpoint, bearer: TestAuth.CreateToken("user-1", "acme", "admin")), Ct);
        var adminKey = await client.SendAsync(Request(HttpMethod.Get, internalEndpoint, TestAuth.ApiKey), Ct);
        var anonymous = await client.SendAsync(Request(HttpMethod.Get, internalEndpoint), Ct);

        service.StatusCode.ShouldBe(HttpStatusCode.OK);
        adminKey.StatusCode.ShouldBe(HttpStatusCode.OK);
        user.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        otherClient.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_with_a_different_azp_is_not_a_service_account()
    {
        ShouldReadNothing(await MeAsync(InternalClient, "some-other-client", ForgedHeaders));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_an_internal_client_id_no_token_is_a_service_account(string? internalClientId)
    {
        // An empty setting must not match a token without azp or with an empty one.
        ShouldReadNothing(await MeAsync(internalClientId, "", ForgedHeaders));
        ShouldReadNothing(await MeAsync(internalClientId, InternalClient, ForgedHeaders));
    }

    [Theory]
    [InlineData("bad value<script>")]
    [InlineData("has space")]
    [InlineData("tooLong-0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task Service_account_header_values_failing_the_check_are_rejected(string value)
    {
        var body = await MeAsync(InternalClient, InternalClient,
            ("X-Calling-Service", value), ("X-On-Behalf-Of-User", value), ("X-Tenant-Id", value));

        body.GetProperty("isServiceAccount").GetBoolean().ShouldBeTrue();
        body.GetProperty("callingService").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("onBehalfOfUser").ValueKind.ShouldBe(JsonValueKind.Null);
        // A service account takes its tenant from the header only; a rejected header means no tenant, never the token's claim.
        body.GetProperty("tenantId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Jwt_tenant_claim_with_unsafe_characters_is_treated_as_no_tenant()
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-1", "bad tenant<script>", "requester");

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Get, TenantEndpoint, bearer: token), Ct);

        // A user token cannot supply X-Tenant-Id, so a token without a usable tenant is forbidden (the API key gets 400).
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Jwt_signed_with_another_key_is_401()
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-1", "acme", "manager");
        var tampered = token[..^4] + (token.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        var response = await app.GetTestClient().SendAsync(Request(HttpMethod.Post, Approve, bearer: tampered), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_key_gets_every_permission()
    {
        await using var app = await TestHost.StartAsync();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", ApiKeyDefaults.AdminUserId),
                    new Claim(ApiKeyDefaults.AuthMethodClaim, ApiKeyDefaults.AuthMethodApiKey),
                ], ApiKeyDefaults.Scheme)),
        };

        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionProvider>().GetPermissionsAsync(
            scope.ServiceProvider.GetRequiredService<ICurrentUser>(), Ct);

        permissions.ShouldBe(Permissions.All, ignoreOrder: true);
        Permissions.All.Count.ShouldBe(15);
    }

    [Fact]
    public async Task Endpoint_without_attribute_is_401_when_anonymous()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/auth-test/no-attribute", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Endpoint_without_attribute_is_open_to_any_authenticated_user()
    {
        await using var app = await TestHost.StartAsync();
        var token = TestAuth.CreateToken("user-1", "acme");

        var response = await app.GetTestClient().SendAsync(
            Request(HttpMethod.Get, "/api/v1/auth-test/no-attribute", bearer: token), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bare_401_from_the_pipeline_is_problem_details()
    {
        await using var app = await TestHost.StartAsync();

        var response = await app.GetTestClient().GetAsync(Me, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().ShouldBe(401);
    }

    [Fact]
    public async Task Health_and_openapi_stay_anonymous()
    {
        await using var app = await TestHost.StartAsync();
        var client = app.GetTestClient();

        (await client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/openapi/v1.json", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_key_has_its_own_rate_limit_partition()
    {
        await using var app = await TestHost.StartAsync(new()
        {
            ["RateLimiting:PermitLimit"] = "2",
            ["RateLimiting:WindowSeconds"] = "60",
        });
        var client = app.GetTestClient();

        var keyed = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            keyed.Add((await client.SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey), Ct)).StatusCode);
        }

        keyed.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        // The exhausted key partition does not affect other callers.
        (await client.GetAsync("/api/v1/test/ping", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_key_calls_do_not_consume_the_ip_partition()
    {
        await using var app = await TestHost.StartAsync(new()
        {
            ["RateLimiting:PermitLimit"] = "2",
            ["RateLimiting:WindowSeconds"] = "60",
        });
        var client = app.GetTestClient();

        for (var i = 0; i < 2; i++)
        {
            await client.SendAsync(Request(HttpMethod.Get, Me, TestAuth.ApiKey), Ct);
        }

        for (var i = 0; i < 2; i++)
        {
            (await client.GetAsync("/api/v1/test/ping", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }
}
