using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace ProcureAi.AppHost.Tests;

/// <summary>
/// Plan 07 Task 5: the AppHost stays the source of truth for wiring. The umbrella chart <c>deploy/charts/procure-ai</c> must give every app
/// the same environment variable names the AppHost gives it (values differ: hosts, TLS, secrets by reference), so a setting added to the
/// AppHost and forgotten in the chart (or the other way round) fails here. Renders the umbrella with <c>helm template</c>, with its default
/// values and, when the GitOps repo is cloned next to this one, with <c>envs/local/procure-ai.yaml</c> too. Skipped without <c>helm</c>.
/// </summary>
public sealed partial class ChartParityTests
{
    /// <summary>The AppHost resource → the chart workloads that run it (Deployment, Job or CronJob names). Every AppHost app must be here.</summary>
    private static readonly Dictionary<string, string[]> Workloads = new(StringComparer.Ordinal)
    {
        ["access-api"] = ["Deployment/access-api"],
        ["approvals-api"] = ["Deployment/approvals-api"],
        ["assistant-api"] = ["Deployment/assistant-api"],
        ["audit-api"] = ["Deployment/audit-api"],
        ["budgets-api"] = ["Deployment/budgets-api"],
        ["invoicing-api"] = ["Deployment/invoicing-api"],
        ["notifications-api"] = ["Deployment/notifications-api"],
        ["purchasing-api"] = ["Deployment/purchasing-api"],
        ["requests-api"] = ["Deployment/requests-api"],
        ["suppliers-api"] = ["Deployment/suppliers-api"],
        ["access-migrate"] = ["Job/access-migrate"],
        ["approvals-migrate"] = ["Job/approvals-migrate"],
        ["assistant-migrate"] = ["Job/assistant-migrate"],
        ["audit-migrate"] = ["Job/audit-migrate"],
        ["budgets-migrate"] = ["Job/budgets-migrate"],
        ["invoicing-migrate"] = ["Job/invoicing-migrate"],
        ["purchasing-migrate"] = ["Job/purchasing-migrate"],
        ["requests-migrate"] = ["Job/requests-migrate"],
        ["suppliers-migrate"] = ["Job/suppliers-migrate"],
        ["gateway"] = ["Deployment/gateway"],
        ["simulators"] = ["Deployment/simulators"],
        ["web"] = ["Deployment/web"],
        // The demo data: a PostSync Job seeds a fresh cluster, a CronJob resets at night (plan 07 ruling 13).
        ["demo-data"] = ["Job/demo-seed", "CronJob/demo-reset"],
    };

    /// <summary>
    /// Names that differ by design, ignored on both sides: Aspire's own (Global Constraints: <c>OTEL_*</c>, which the chart sets for the
    /// collector, <c>ASPNETCORE_URLS</c>, <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>, <c>DOTNET_*</c>, which values set and
    /// <see cref="Chart_dotnet_caps_and_pool_budget_match_apphost"/> checks; plus what Aspire 13 adds for every reference and endpoint,
    /// read by none of our apps: the upper-case connection properties <c>&lt;NAME&gt;_HOST</c>, <c>_PORT</c>, <c>_URI</c>, ... and
    /// <c>&lt;RESOURCE&gt;_HTTP</c> next to <c>ConnectionStrings__*</c> / <c>services__*</c>, the dashboard's console formatter, the https port
    /// of the gateway's dev-certificate endpoint and the dev server's <c>NODE_ENV</c>), the Serilog OTLP endpoint (set by the AppHost only
    /// while a dashboard runs, by the chart always: the collector) and the optional model settings <c>AI__*</c> (passed only when configured).
    /// </summary>
    private static bool IgnoredEverywhere(string name) =>
        name.StartsWith("OTEL_", StringComparison.Ordinal)
        || name.StartsWith("DOTNET_", StringComparison.Ordinal)
        || name is "ASPNETCORE_URLS" or "ASPNETCORE_FORWARDEDHEADERS_ENABLED" or "ASPNETCORE_HTTPS_PORT" or "LOGGING__CONSOLE__FORMATTERNAME"
            or "NODE_ENV" or "Serilog__WriteTo__OpenTelemetry__Args__endpoint"
        || AspireReferenceProperty().IsMatch(name)
        || name.StartsWith("AI__", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Z0-9_]+_(HOST|PORT|URI|PASSWORD|USERNAME|DATABASENAME|JDBCCONNECTIONSTRING|AUTHENTICATIONDATABASE|AUTHENTICATIONMECHANISM|HTTP|HTTPS)$")]
    private static partial Regex AspireReferenceProperty();

    /// <summary>
    /// What only the cluster needs: the local CA for TLS to Keycloak and MongoDB (ruling 8); for the gateway, the ingress as trusted proxy
    /// and the app's origin for the hubs (Traefik forwards WebSocket upgrades as wss, so the gateway's own origin no longer matches).
    /// </summary>
    private static bool ChartOnly(string app, string name) =>
        name == "SSL_CERT_DIR"
        || (app == "gateway" && (name.StartsWith("ForwardedHeaders__", StringComparison.Ordinal) || name.StartsWith("Hubs__AllowedOrigins__", StringComparison.Ordinal)));

    /// <summary>The Angular dev server's own settings under the AppHost (<c>ng serve --port</c>, Node heap); the cluster serves the build with nginx.</summary>
    private static readonly Dictionary<string, string[]> AppHostOnly = new(StringComparer.Ordinal)
    {
        ["web"] = ["PORT", "NODE_OPTIONS"],
    };

    [Fact]
    public async Task Chart_env_matches_apphost_env_for_every_app()
    {
        var helm = Helm.FindOnPath();
        if (helm is null)
        {
            Assert.Skip("helm is not on PATH: the umbrella chart cannot be rendered (CI's charts job and the PC have it).");
        }

        var appHost = await AppHostEnvironmentsAsync();
        appHost.Keys.Where(k => !Workloads.ContainsKey(k))
            .ShouldBeEmpty("AppHost apps the chart does not run (add them to the umbrella and to Workloads)");

        foreach (var (label, rendered) in await Helm.RenderUmbrellaVariantsAsync(helm))
        {
            var chart = RenderedChart.Workloads(rendered);
            var problems = new List<string>();
            foreach (var (app, workloads) in Workloads)
            {
                var expected = appHost.TryGetValue(app, out var env)
                    ? env.Keys.Where(n => !IgnoredEverywhere(n) && !(AppHostOnly.TryGetValue(app, out var only) && only.Contains(n))).ToHashSet(StringComparer.Ordinal)
                    : throw new Xunit.Sdk.XunitException($"AppHost resource {app} is gone; remove it from Workloads.");
                foreach (var workload in workloads)
                {
                    if (!chart.TryGetValue(workload, out var actualEnv))
                    {
                        problems.Add($"{app}: {workload} is not rendered");
                        continue;
                    }

                    var actual = actualEnv.Keys.Where(n => !IgnoredEverywhere(n) && !ChartOnly(app, n)).ToHashSet(StringComparer.Ordinal);
                    var missing = expected.Except(actual).Order(StringComparer.Ordinal).ToList();
                    // A migrate Job shares its app's env and secretEnv (plan 07 Task 5), so it may carry more than the AppHost's migrate resource.
                    var extra = workload.StartsWith("Job/", StringComparison.Ordinal) && app.EndsWith("-migrate", StringComparison.Ordinal)
                        ? []
                        : actual.Except(expected).Order(StringComparer.Ordinal).ToList();
                    if (missing.Count > 0)
                    {
                        problems.Add($"{app} ({workload}) missing: {string.Join(", ", missing)}");
                    }

                    if (extra.Count > 0)
                    {
                        problems.Add($"{app} ({workload}) extra: {string.Join(", ", extra)}");
                    }
                }
            }

            problems.ShouldBeEmpty($"chart rendered with {label}");
        }
    }

    /// <summary>
    /// The pool budget of <c>ServiceResourcesTests.Pool_sizes_fit_max_connections</c> holds in the cluster too: every Postgres API and its
    /// migration cap their pools at <c>Resources:Postgres:PoolSizePerService</c>, and each API runs one replica (the budget counts one
    /// instance per service). The .NET pods keep the AppHost's GC caps (<c>Resources:DotNet</c>).
    /// </summary>
    [Fact]
    public async Task Chart_dotnet_caps_and_pool_budget_match_apphost()
    {
        var helm = Helm.FindOnPath();
        if (helm is null)
        {
            Assert.Skip("helm is not on PATH: the umbrella chart cannot be rendered (CI's charts job and the PC have it).");
        }

        var limits = ResourcesOptions.Load(new ConfigurationBuilder().AddJsonFile("apphost.appsettings.json").Build());
        var appHost = await AppHostEnvironmentsAsync();
        foreach (var (label, rendered) in await Helm.RenderUmbrellaVariantsAsync(helm))
        {
            var chart = RenderedChart.Workloads(rendered);
            var replicas = RenderedChart.Replicas(rendered);
            foreach (var (app, env) in appHost)
            {
                foreach (var workload in Workloads[app])
                {
                    var actual = chart[workload];
                    if (env.ContainsKey("Database__MaxPoolSize"))
                    {
                        actual.ShouldContainKeyAndValue(
                            "Database__MaxPoolSize", limits.Postgres.PoolSizePerService.ToString(CultureInfo.InvariantCulture), $"{label}: {workload}");
                        if (workload.StartsWith("Deployment/", StringComparison.Ordinal))
                        {
                            replicas.ShouldContainKeyAndValue(workload, 1, $"{label}: {workload} (the pool budget counts one instance)");
                        }
                    }

                    if (env.ContainsKey("DOTNET_GCHeapHardLimit"))
                    {
                        actual.ShouldContainKeyAndValue("DOTNET_GCHeapHardLimit", limits.DotNet.GcHeapHardLimit, $"{label}: {workload}");
                        actual.ShouldContainKeyAndValue("DOTNET_gcServer", limits.DotNet.GcServer.ToString(CultureInfo.InvariantCulture), $"{label}: {workload}");
                        actual.ShouldContainKeyAndValue(
                            "DOTNET_GCConserveMemory", limits.DotNet.GcConserveMemory.ToString(CultureInfo.InvariantCulture), $"{label}: {workload}");
                    }
                }
            }
        }
    }

    /// <summary>The environment variable names (and plain values) of every app resource of the AppHost model: projects and the web app.</summary>
    private static async Task<Dictionary<string, Dictionary<string, string?>>> AppHostEnvironmentsAsync()
    {
        await using var scope = await ServiceResourcesTests.ModelScope.BuildAsync();
        var result = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var apps = scope.Model.Resources.OfType<ProjectResource>().Cast<IResource>()
            .Concat(scope.Model.Resources.Where(r => r.Name == ResourceNames.Web));
        foreach (var app in apps)
        {
            var env = await ServiceResourcesTests.EnvironmentOfAsync(app);
            result[app.Name] = env.ToDictionary(kv => kv.Key, kv => kv.Value as string, StringComparer.Ordinal);
        }

        return result;
    }

    internal static class Helm
    {
        public static string? FindOnPath()
        {
            var names = OperatingSystem.IsWindows() ? new[] { "helm.exe", "helm" } : ["helm"];
            return (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(dir => names.Select(n => Path.Combine(dir, n)))
                .FirstOrDefault(File.Exists);
        }

        /// <summary>The umbrella rendered with its defaults (demo switches on) and, when the GitOps repo is cloned next to procure-ai, with the local env file.</summary>
        public static async Task<List<(string Label, string Rendered)>> RenderUmbrellaVariantsAsync(string helm)
        {
            var root = RepositoryRoot();
            var umbrella = Path.Combine(root, "deploy", "charts", "procure-ai");
            Directory.Exists(umbrella).ShouldBeTrue($"umbrella chart {umbrella} is missing");
            await RunAsync(helm, ["dependency", "build", umbrella, "--skip-refresh"]);

            var variants = new List<(string, string)>
            {
                // The demo data workloads exist only with the demo switches on (off by default, like every demo feature).
                ("default values with the demo switches on", await RunAsync(helm, [
                    "template", "procure-ai", umbrella, "--namespace", "procure", "--set", "global.imageTag=test",
                    "--set", "global.demo.seed=true", "--set", "global.demo.reset=true"])),
            };
            if (LocalEnvFile(root) is { } local)
            {
                variants.Add(($"{local}", await RunAsync(helm, ["template", "procure-ai", umbrella, "--namespace", "procure", "-f", local, "--set", "global.imageTag=test"])));
            }

            return variants;
        }

        private static string? LocalEnvFile(string root)
        {
            // procure-ai and procure-ai-gitops are siblings in C:\Projects\2026; a worktree sits one level deeper (procure-ai.worktrees\<branch>).
            for (var dir = new DirectoryInfo(root).Parent; dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "procure-ai-gitops", "envs", "local", "procure-ai.yaml");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static async Task<string> RunAsync(string helm, string[] args)
        {
            var start = new ProcessStartInfo(helm, args) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            process.ExitCode.ShouldBe(0, $"helm {string.Join(' ', args)}: {await error}");
            return await output;
        }

        private static string RepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcureAi.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("ProcureAi.slnx not found above the test output folder.");
        }
    }

    /// <summary>
    /// Line-based reading of <c>helm template</c> output (no YAML library in the tests). Relies on how procure-app writes env: an
    /// <c>env:</c> key, then entries <c>- name: X</c> two spaces deeper, each followed by <c>value: "…"</c> or <c>valueFrom:</c>.
    /// </summary>
    internal static class RenderedChart
    {
        /// <summary><c>Kind/name</c> → env (name → plain value, or null for valueFrom) of the workload's main container.</summary>
        public static Dictionary<string, Dictionary<string, string?>> Workloads(string rendered)
        {
            var result = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            foreach (var lines in Documents(rendered))
            {
                var kind = Kind(lines);
                if (kind is not ("Deployment" or "Job" or "CronJob"))
                {
                    continue;
                }

                var key = $"{kind}/{Name(lines)}";
                result.ContainsKey(key).ShouldBeFalse($"{key} rendered twice");
                result[key] = MainContainerEnv(lines);
            }

            return result;
        }

        /// <summary><c>Deployment/name</c> → <c>spec.replicas</c>.</summary>
        public static Dictionary<string, int> Replicas(string rendered)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var lines in Documents(rendered).Where(l => Kind(l) == "Deployment"))
            {
                var line = lines.FirstOrDefault(l => l.StartsWith("  replicas: ", StringComparison.Ordinal));
                line.ShouldNotBeNull($"Deployment {Name(lines)} sets no replicas");
                result[$"Deployment/{Name(lines)}"] = int.Parse(line["  replicas: ".Length..].Trim(), CultureInfo.InvariantCulture);
            }

            return result;
        }

        private static IEnumerable<string[]> Documents(string rendered) =>
            rendered.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n---", StringSplitOptions.RemoveEmptyEntries).Select(d => d.Split('\n'));

        private static string? Kind(string[] lines) =>
            lines.FirstOrDefault(l => l.StartsWith("kind: ", StringComparison.Ordinal))?["kind: ".Length..].Trim();

        private static string Name(string[] lines)
        {
            var metadata = Array.IndexOf(lines, "metadata:");
            return lines.Skip(metadata + 1).First(l => l.StartsWith("  name: ", StringComparison.Ordinal))["  name: ".Length..].Trim();
        }

        /// <summary>The env of the first entry under <c>containers:</c> (init containers are skipped).</summary>
        private static Dictionary<string, string?> MainContainerEnv(string[] lines)
        {
            var containers = Array.FindIndex(lines, l => l.TrimEnd().EndsWith(" containers:", StringComparison.Ordinal) && l.Trim() == "containers:");
            containers.ShouldBeGreaterThan(0, "a workload without containers");
            var containersIndent = Indent(lines[containers]);
            var env = new Dictionary<string, string?>(StringComparer.Ordinal);
            var firstContainerSeen = false;
            for (var i = containers + 1; i < lines.Length; i++)
            {
                var indent = Indent(lines[i]);
                if (lines[i].Trim().Length == 0)
                {
                    continue;
                }

                if (indent <= containersIndent && !lines[i].TrimStart().StartsWith('-'))
                {
                    break;
                }

                if (lines[i].TrimStart().StartsWith("- name: ", StringComparison.Ordinal) && indent <= containersIndent + 2)
                {
                    if (firstContainerSeen)
                    {
                        break;
                    }

                    firstContainerSeen = true;
                    continue;
                }

                if (lines[i].Trim() != "env:")
                {
                    continue;
                }

                for (var j = i + 1; j < lines.Length && (lines[j].Trim().Length == 0 || Indent(lines[j]) > indent); j++)
                {
                    var entry = lines[j].Trim();
                    if (Indent(lines[j]) == indent + 2 && entry.StartsWith("- name: ", StringComparison.Ordinal))
                    {
                        var name = entry["- name: ".Length..].Trim().Trim('"');
                        var next = j + 1 < lines.Length ? lines[j + 1].Trim() : "";
                        env[name] = next.StartsWith("value: ", StringComparison.Ordinal) ? next["value: ".Length..].Trim().Trim('"') : null;
                    }
                }

                break;
            }

            return env;
        }

        private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    }
}
