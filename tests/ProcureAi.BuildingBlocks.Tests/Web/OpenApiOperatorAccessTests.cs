using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>
/// Swagger UI from the browser (plan 08b Task 2). The documents are fetched through the gateway, which forwards the scheme the
/// browser used as <c>X-Forwarded-Proto</c>: a service behind a trusted proxy must name that scheme in its <c>servers</c>, or an
/// https page calls an http URL and the browser blocks it as mixed content. A service takes only the scheme from a proxy, never
/// the client address. Tenant endpoints offer <c>X-Tenant-Id</c> in the Authorize dialog next to the admin key.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public class OpenApiOperatorAccessTests
{
    private const string Document = "/openapi/v1.json";
    private const string Ping = "/api/v1/test/ping";
    private const string Proxy = "10.0.0.1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Get(string url, string remoteIp, string? forwardedProto = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Host = "procure.example";
        request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return request;
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string[] ServerUrls(JsonElement document) =>
        [.. document.GetProperty("servers").EnumerateArray().Select(s => s.GetProperty("url").GetString()!)];

    [Fact]
    public async Task The_document_names_the_scheme_a_trusted_proxy_forwards_and_ignores_it_from_anyone_else()
    {
        await using var app = await TestHost.StartAsync(new() { ["ForwardedHeaders:KnownProxies:0"] = Proxy });
        var client = app.GetTestClient();

        ServerUrls(await DocumentAsync(client, Get(Document, Proxy, forwardedProto: "https"))).ShouldBe(["https://procure.example/"]);
        ServerUrls(await DocumentAsync(client, Get(Document, "10.0.0.9", forwardedProto: "https"))).ShouldBe(["http://procure.example/"]);
    }

    [Fact]
    public async Task By_default_a_trusted_proxy_cannot_set_the_client_address()
    {
        // The services' setting: the scheme only. Forwarded addresses must not buy new rate-limit allowances.
        await using var app = await TestHost.StartAsync(new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = Proxy,
            ["RateLimiting:PermitLimit"] = "2",
            ["RateLimiting:WindowSeconds"] = "60",
        });
        var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        foreach (var ip in new[] { "203.0.113.7", "203.0.113.8", "203.0.113.9" })
        {
            statuses.Add((await client.SendAsync(Get(Ping, Proxy, forwardedProto: "https", forwardedFor: ip), Ct)).StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
    }

    [Theory]
    [InlineData("XForwardedHost")]
    [InlineData("XForwardedProto, XForwardedPrefix")]
    [InlineData("None")]
    public async Task Only_the_scheme_and_the_client_address_may_be_forwarded(string headers)
    {
        await Should.ThrowAsync<OptionsValidationException>(() => TestHost.StartAsync(new() { ["ForwardedHeaders:Headers"] = headers }));
    }

    [Fact]
    public async Task Tenant_endpoints_ask_for_the_admin_key_with_x_tenant_id_and_others_do_not()
    {
        await using var app = await TestHost.StartAsync();
        var document = await DocumentAsync(app.GetTestClient(), Get(Document, "10.0.0.9"));

        var tenant = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("TenantId");
        tenant.GetProperty("type").GetString().ShouldBe("apiKey");
        tenant.GetProperty("in").GetString().ShouldBe("header");
        tenant.GetProperty("name").GetString().ShouldBe("X-Tenant-Id");
        tenant.GetProperty("description").GetString()!.ShouldContain("acme");

        // The document-wide default stays: the key alone, or a Keycloak login.
        Requirements(document.GetProperty("security")).ShouldBe(["ApiKey", "OAuth2"]);

        var paths = document.GetProperty("paths");
        Requirements(paths.GetProperty("/api/v1/auth-test/tenant").GetProperty("get").GetProperty("security"))
            .ShouldBe(["ApiKey+TenantId", "OAuth2"]);
        paths.GetProperty("/api/v1/auth-test/me").GetProperty("get").TryGetProperty("security", out _).ShouldBeFalse();
    }

    private static string[] Requirements(JsonElement security) =>
        [.. security.EnumerateArray().Select(r => string.Join('+', r.EnumerateObject().Select(o => o.Name).Order(StringComparer.Ordinal)))];
}
