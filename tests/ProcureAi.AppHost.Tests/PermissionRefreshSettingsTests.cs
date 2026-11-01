using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// Fast check (no containers) of a relation between two settings in different places (plan 06 Task 17): after an <c>access-matrix</c>
/// push the browser reads /bff/user at once and again after the Angular <c>cache.trailingReloadMs</c>. The gateway keeps a user's
/// permissions for <c>AccessClient:CacheDuration</c> and cannot hear the change, so the immediate read can refill its cache with the old
/// permissions. The trailing read only gets the new ones if that cache entry has expired by then: the cache must be clearly shorter.
/// </summary>
public sealed class PermissionRefreshSettingsTests
{
    /// <summary>Room for the time between the push and the immediate read reaching the gateway, and for timer jitter.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Gateway_permission_cache_expires_well_before_the_trailing_reload()
    {
        var root = RepositoryRoot();
        using var gateway = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Gateway", "ProcureAi.Gateway", "appsettings.json")));
        var cache = TimeSpan.Parse(gateway.RootElement.GetProperty("AccessClient").GetProperty("CacheDuration").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

        var web = Path.Combine(root, "src", "Web", "procure-ai-web");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(web, "public", "config.json")));
        var runtime = TimeSpan.FromMilliseconds(config.RootElement.GetProperty("cache").GetProperty("trailingReloadMs").GetInt32());
        var environment = Regex.Match(File.ReadAllText(Path.Combine(web, "src", "environments", "environment.ts")), @"trailingReloadMs:\s*([\d_]+)");
        environment.Success.ShouldBeTrue("environment.ts has no cache.trailingReloadMs");
        var buildDefault = TimeSpan.FromMilliseconds(int.Parse(environment.Groups[1].Value.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture));

        (cache + Margin).ShouldBeLessThanOrEqualTo(runtime, "config.json cache.trailingReloadMs vs the gateway's AccessClient:CacheDuration");
        (cache + Margin).ShouldBeLessThanOrEqualTo(buildDefault, "environment.ts cache.trailingReloadMs vs the gateway's AccessClient:CacheDuration");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProcureAi.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("ProcureAi.slnx not found above " + AppContext.BaseDirectory);
    }
}
