using Microsoft.Extensions.Configuration;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// Fast check (no containers): Testcontainers-based tests must use exactly the caps the AppHost uses,
/// so tests/testcontainers.settings.json must match the AppHost's Resources section value for value.
/// </summary>
public sealed class ResourceSettingsTests
{
    [Fact]
    public void Testcontainers_settings_match_the_AppHost_Resources_section()
    {
        var appHost = Flatten("apphost.appsettings.json");
        var testcontainers = Flatten("testcontainers.settings.json");

        appHost.ShouldNotBeEmpty();
        testcontainers.Keys.Except(appHost.Keys).ShouldBeEmpty("keys only in testcontainers.settings.json");
        appHost.Keys.Except(testcontainers.Keys).ShouldBeEmpty("keys missing from testcontainers.settings.json");
        foreach (var (key, value) in appHost)
        {
            testcontainers[key].ShouldBe(value, $"value of {key}");
        }
    }

    [Fact]
    public void Testcontainers_settings_pass_the_AppHost_validation()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile("testcontainers.settings.json").Build();
        Should.NotThrow(() => ResourcesOptions.Load(configuration));
    }

    private static Dictionary<string, string?> Flatten(string file) =>
        new ConfigurationBuilder().AddJsonFile(file, optional: false).Build()
            .GetSection(ResourcesOptions.SectionName)
            .AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
