using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// Plan 07 Task 4: the platform chart's Debezium Servers capture exactly what the AppHost's do. The AppHost derives them from the services'
/// <see cref="ServiceSpec"/>s (one <c>debezium-&lt;database&gt;</c> per Postgres database with captured tables, one <c>debezium-mongo</c> for
/// every captured MongoDB collection); the chart lists them in values.yaml (<c>debezium.postgres</c>, <c>debezium.mongo</c>). Renders the
/// chart with <c>helm template</c> and its default (local) values and compares the environment the Debezium pods get.
/// </summary>
public sealed class ChartDebeziumTests
{
    [Fact]
    public async Task Chart_debezium_sources_match_apphost()
    {
        var helm = FindOnPath("helm");
        if (helm is null)
        {
            Assert.Skip("helm is not on PATH: the chart's Debezium sources cannot be rendered (CI's charts job and the PC have it).");
        }

        await using var scope = await ServiceResourcesTests.ModelScope.BuildAsync();
        var appHost = new Dictionary<string, Dictionary<string, string>>();
        foreach (var debezium in scope.Model.Resources.OfType<ContainerResource>().Where(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal)))
        {
            var env = await ServiceResourcesTests.EnvironmentOfAsync(debezium);
            appHost[debezium.Name] = env.Where(kv => kv.Value is string).ToDictionary(kv => kv.Key, kv => (string)kv.Value);
        }

        var chart = StatefulSetEnvironments(await RenderAsync(helm, "templates/debezium.yaml"));

        chart.Keys.ShouldBe(appHost.Keys, ignoreOrder: true, "one Debezium StatefulSet per AppHost Debezium resource");
        foreach (var (name, expected) in appHost)
        {
            var actual = chart[name];
            foreach (var key in new[]
            {
                "DEBEZIUM_SOURCE_DATABASE_DBNAME", "DEBEZIUM_SOURCE_TOPIC_PREFIX", "DEBEZIUM_SOURCE_SLOT_NAME", "DEBEZIUM_SOURCE_TABLE_INCLUDE_LIST",
                "DEBEZIUM_SOURCE_COLLECTION_INCLUDE_LIST", "DEBEZIUM_SOURCE_MONGODB_USER",
            })
            {
                if (expected.TryGetValue(key, out var value))
                {
                    actual.ShouldContainKeyAndValue(key, value, $"{name} {key}");
                }
                else
                {
                    actual.ShouldNotContainKey(key, $"{name} {key}");
                }
            }

            // The deployment fails loudly on a lost offset (plan 07: self-heal off in Kubernetes), whatever the local AppHost does.
            actual.ShouldContainKeyAndValue(ServiceResources.SelfHealEnvironmentVariable, "false", name);
        }
    }

    private static async Task<string> RenderAsync(string helm, string template)
    {
        var start = new ProcessStartInfo(helm, ["template", "procure-platform", ChartPath(), "--set", "global.imageTag=test", "--show-only", template])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        process.ExitCode.ShouldBe(0, await error);
        return await output;
    }

    /// <summary>
    /// The plain <c>name</c>/<c>value</c> environment of the first container of each StatefulSet in a rendered template. Line based: our
    /// template writes every entry as <c>- name: X</c> followed by <c>value: "Y"</c> (secretKeyRef entries have no value line).
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> StatefulSetEnvironments(string rendered)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var document in rendered.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n---\n"))
        {
            var lines = document.Split('\n');
            if (!lines.Any(l => l == "kind: StatefulSet"))
            {
                continue;
            }

            var metadata = Array.IndexOf(lines, "metadata:");
            var name = lines.Skip(metadata + 1).First(l => l.StartsWith("  name: ", StringComparison.Ordinal))["  name: ".Length..].Trim();
            var containers = Array.FindIndex(lines, l => l.TrimEnd() == "      containers:");
            containers.ShouldBeGreaterThan(0, $"{name} has no containers");
            var env = new Dictionary<string, string>();
            for (var i = containers; i < lines.Length - 1; i++)
            {
                var entry = lines[i].Trim();
                var next = lines[i + 1].Trim();
                if (entry.StartsWith("- name: ", StringComparison.Ordinal) && next.StartsWith("value: ", StringComparison.Ordinal))
                {
                    env[entry["- name: ".Length..].Trim()] = next["value: ".Length..].Trim().Trim('"');
                }
            }

            result[name] = env;
        }

        return result;
    }

    private static string ChartPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcureAi.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("ProcureAi.slnx not found above the test output folder."),
            "deploy", "charts", "procure-platform");
    }

    private static string? FindOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(n => Path.Combine(dir, n)))
            .FirstOrDefault(File.Exists);
    }
}
