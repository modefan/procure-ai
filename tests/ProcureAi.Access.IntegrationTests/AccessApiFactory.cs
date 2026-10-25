using Microsoft.AspNetCore.Hosting;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>The real Access API (its Program) on a test server, against the shared Postgres, RabbitMQ and Redis.</summary>
public sealed class AccessApiFactory(AccessInfraFixture infra)
    : ServiceApiFactory<Program>("accessdb", infra.DbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    public const string InternalClientId = "procure-internal";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:InternalClientId", InternalClientId);
    }
}
