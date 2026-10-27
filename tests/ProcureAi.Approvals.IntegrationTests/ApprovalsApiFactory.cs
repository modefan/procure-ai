using Microsoft.AspNetCore.Hosting;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// The real Approvals API (its Program) on a test server, against a Postgres database, RabbitMQ and Redis, with the stub Access
/// service. <paramref name="settings"/> overrides configuration (e.g. timeouts of seconds).
/// </summary>
public sealed class ApprovalsApiFactory(
    string database, string rabbit, string redis, StubAccessServer access, IReadOnlyDictionary<string, string?>? settings = null)
    : ServiceApiFactory<Program>("approvalsdb", database, rabbit, redis)
{
    /// <summary>The client ID whose tokens count as another service's (policy <c>internal</c>).</summary>
    public const string InternalClientId = "procure-internal";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:InternalClientId", InternalClientId);
        foreach (var (key, value) in access.Settings.Concat(settings ?? new Dictionary<string, string?>()))
        {
            builder.UseSetting(key, value);
        }
    }
}
