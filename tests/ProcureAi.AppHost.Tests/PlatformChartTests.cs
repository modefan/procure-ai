using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// Fast check (no containers): the platform chart sizes Postgres, MongoDB, Redis, RabbitMQ, Keycloak, Mailpit and Debezium with exactly
/// the AppHost's Resources section, so the cluster and the AppHost cannot drift apart (plan 07 Task 3). The chart keeps them under
/// `appHostResources:`, one flow mapping per line in the AppHost's own key names.
/// </summary>
public sealed partial class PlatformChartTests
{
    private static readonly string[] PlatformComponents = ["Postgres", "Mongo", "Redis", "RabbitMq", "Keycloak", "Mailpit", "Debezium"];

    [Fact]
    public void Platform_chart_values_mirror_the_AppHost_Resources()
    {
        var chart = ReadChartResources("platform.values.yaml");
        var appHost = new ConfigurationBuilder().AddJsonFile("apphost.appsettings.json", optional: false).Build()
            .GetSection(ResourcesOptions.SectionName);

        chart.Keys.ShouldBe(PlatformComponents, ignoreOrder: true);
        foreach (var component in PlatformComponents)
        {
            var expected = appHost.GetSection(component).GetChildren().Select(c => $"{c.Key}={c.Value}");
            chart[component].Select(kv => $"{kv.Key}={kv.Value}").ShouldBe(expected, ignoreOrder: true, $"appHostResources.{component}");
        }
    }

    private static Dictionary<string, Dictionary<string, string?>> ReadChartResources(string file)
    {
        var result = new Dictionary<string, Dictionary<string, string?>>();
        var inBlock = false;
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith("appHostResources:", StringComparison.Ordinal))
            {
                inBlock = true;
                continue;
            }

            if (!inBlock || line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                break;
            }

            var match = ComponentLine().Match(line);
            match.Success.ShouldBeTrue($"appHostResources line is not `  Name: {{ Key: value, ... }}`: {line}");
            result[match.Groups["name"].Value] = match.Groups["body"].Value
                .Split(',', StringSplitOptions.TrimEntries)
                .Select(pair => pair.Split(':', 2, StringSplitOptions.TrimEntries))
                .ToDictionary(kv => kv[0], kv => (string?)kv[1].Trim('"'));
        }

        return result;
    }

    [GeneratedRegex(@"^  (?<name>\w+): \{ (?<body>.*) \}\s*$")]
    private static partial Regex ComponentLine();
}
