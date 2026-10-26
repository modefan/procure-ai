using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Web.Auth;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>
/// The Keycloak admin client (<c>procure-access-admin</c>) is not a service identity: its tokens carry neither the
/// <c>procure-api</c> audience nor <c>azp = procure-internal</c>, so a service API refuses them. Checked with real tokens
/// from the real Keycloak (the other tests validate tokens against a local key).
/// </summary>
[Collection(AccessInfraCollection.Name)]
public sealed class AdminTokenRefusedTests(AccessInfraFixture infra, LazyKeycloak lazyKeycloak) : IAsyncLifetime
{
    private const string Internal = "/api/v1/access/internal/effective?userId=u-1&roles=requester";

    private KeycloakFixture _keycloak = null!;
    private RealTokensFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _keycloak = await lazyKeycloak.GetAsync();
        _factory = new RealTokensFactory(infra, _keycloak);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<HttpStatusCode> CallAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Internal);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", "acme");
        using var response = await _client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Internal_client_token_is_accepted_by_the_internal_endpoint()
    {
        // The control: the same pipeline accepts a real token of the service identity, so the refusal below means something.
        (await CallAsync(await _keycloak.InternalTokenAsync())).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Access_admin_client_token_is_refused_by_the_internal_endpoint()
    {
        (await CallAsync(await _keycloak.AccessAdminTokenAsync())).ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>The Access API that validates tokens against the real Keycloak (its discovery document and signing keys).</summary>
    private sealed class RealTokensFactory(AccessInfraFixture infra, KeycloakFixture realKeycloak) : AccessApiFactory(infra, realKeycloak)
    {
        private readonly string _authority = $"{realKeycloak.BaseUrl}/realms/{KeycloakFixture.Realm}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            var authority = _authority;
            builder.UseSetting("Auth:Authority", authority);
            builder.UseSetting("Auth:Audience", "procure-api");
            builder.ConfigureTestServices(services =>
                // Registered after the base class's PostConfigure (which installs the local test key), so it wins.
                services.PostConfigure<JwtBearerOptions>(ApiKeyDefaults.BearerScheme, options =>
                {
                    options.Authority = authority;
                    options.RequireHttpsMetadata = false;
                    options.TokenValidationParameters.IssuerSigningKey = null;
                    options.TokenValidationParameters.ValidIssuer = null;
                    options.TokenValidationParameters.ValidAudience = "procure-api";
                }));
        }
    }
}
