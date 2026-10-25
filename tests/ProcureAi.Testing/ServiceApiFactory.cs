using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Testing;

/// <summary>
/// A service's real application (its <c>Program</c>) on a test server, against the fixtures' Postgres, RabbitMQ and Redis,
/// with tokens validated against the local test key. Several factories on the same infrastructure stand in for several
/// instances of the service.
/// </summary>
/// <param name="databaseConnectionName">The service's connection-string name, e.g. <c>requestsdb</c>.</param>
public class ServiceApiFactory<TProgram>(
    string databaseConnectionName, string databaseConnectionString, string rabbitConnectionString, string redisConnectionString)
    : WebApplicationFactory<TProgram>
    where TProgram : class
{
    public const string OpenApiAuthorizationUrl = "https://login.test/tenant/oauth2/v2.0/authorize";
    public const string OpenApiTokenUrl = "https://login.test/tenant/oauth2/v2.0/token";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting, not ConfigureAppConfiguration: the composition root reads some settings while it builds.
        builder.UseSetting($"ConnectionStrings:{databaseConnectionName}", databaseConnectionString);
        builder.UseSetting("ConnectionStrings:rabbitmq", rabbitConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        builder.UseSetting("Auth:Authority", TestAuth.Issuer);
        builder.UseSetting("Auth:Audience", TestAuth.Audience);
        builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        builder.UseSetting("Auth:AdminApiKeySha256", TestAuth.ApiKeySha256);
        builder.UseSetting("Auth:OpenApi:AuthorizationUrl", OpenApiAuthorizationUrl);
        builder.UseSetting("Auth:OpenApi:TokenUrl", OpenApiTokenUrl);
        builder.UseSetting("Auth:OpenApi:Scopes:openid", "OpenID Connect");
        builder.UseSetting("Auth:OpenApi:Scopes:email", "Email address");
        builder.UseSetting("Wolverine:AutoBuildMessageStorageOnStartup", "CreateOrUpdate");
        builder.UseSetting("Idempotency:WaitTimeout", "00:00:20");

        builder.ConfigureTestServices(services =>
        {
            // Validate tokens against the local test key, without discovery documents or any network call.
            services.PostConfigure<JwtBearerOptions>(ApiKeyDefaults.BearerScheme, options =>
            {
                options.Authority = null;
                options.TokenValidationParameters.IssuerSigningKey = TestAuth.SigningKey;
                options.TokenValidationParameters.ValidIssuer = TestAuth.Issuer;
            });
        });
    }
}
