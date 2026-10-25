using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;

namespace ProcureAi.Testing;

/// <summary>
/// The container caps of <c>tests/testcontainers.settings.json</c> (the same values as the AppHost's Resources section),
/// so a test run on the shared PC never exceeds the budget.
/// </summary>
public static class TestLimits
{
    private static readonly IConfigurationSection Resources = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("testcontainers.settings.json", optional: false)
        .Build()
        .GetSection("Resources");

    /// <summary>The settings of one resource, e.g. <c>Postgres</c> or <c>Redis</c>.</summary>
    public static IConfigurationSection Section(string resource) => Resources.GetSection(resource);

    /// <summary>Caps memory (with no extra swap) and CPUs of the container being created.</summary>
    public static void Apply(CreateContainerParameters parameters, string resource)
    {
        var section = Section(resource);
        var host = parameters.HostConfig ??= new HostConfig();
        host.Memory = long.Parse(section["MemoryMb"]!) * 1024 * 1024;
        host.MemorySwap = host.Memory;
        host.NanoCPUs = long.Parse(section["Cpus"]!) * 1_000_000_000L;
    }
}
