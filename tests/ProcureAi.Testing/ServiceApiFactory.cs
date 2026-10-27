using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProcureAi.BuildingBlocks.Web.Auth;

namespace ProcureAi.Testing;

/// <summary>
/// A service's real application (its <c>Program</c>) on a test server, against the fixtures' Postgres, RabbitMQ and Redis,
/// with tokens validated against the local test key. Several factories on the same infrastructure stand in for several
/// instances of the service. Disposing one closes its idle database connections (see <see cref="DisposeAsync"/>).
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

    /// <summary>
    /// Stops the application, then clears the Npgsql pool of its connection string. A suite starts many instances (several on
    /// databases of their own), and each pool otherwise keeps its idle connections open until Npgsql prunes them (minutes later),
    /// so a full run reached Postgres' <c>max_connections</c> (53300 "too many clients"). Another instance on the same
    /// database is not disturbed: clearing closes idle connections, and connections in use are closed when they are returned.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearPool(new NpgsqlConnection(databaseConnectionString));
    }
}
