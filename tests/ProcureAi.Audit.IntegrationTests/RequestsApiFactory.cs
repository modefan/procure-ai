extern alias RequestsApi;

using Microsoft.AspNetCore.Hosting;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>
/// The real Requests API (its Program, through the extern alias) on a test server, on the Audit tests' Postgres, broker and Redis,
/// with the stub Access and Approvals. Its writes go through the audit interceptor, so they are app changes for the CDC tests.
/// </summary>
public sealed class RequestsApiFactory(
    AuditInfraFixture infra, string requestsDbConnectionString, StubAccessServer access, StubApprovalsServer approvals)
    : ServiceApiFactory<RequestsApi::Program>("requestsdb", requestsDbConnectionString, infra.RabbitConnectionString, infra.RedisConnectionString)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var (key, value) in access.Settings.Concat(approvals.Settings))
        {
            builder.UseSetting(key, value);
        }
    }
}
