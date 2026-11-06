using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace ProcureAi.ArchitectureTests;

/// <summary>
/// One Node version everywhere (plan 07 carry-over 31): <c>.nvmrc</c> (CI's <c>setup-node</c>, local nvm) is the source; the web image's
/// build stage uses the same patch, the web app's <c>engines</c> range allows it, and npm enforces that range (<c>engine-strict</c>), so
/// a wrong Node fails <c>npm ci</c> instead of building with another runtime. CI's build job runs the same check as a shell step
/// while the tests are off.
/// </summary>
public sealed partial class NodeVersionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    private static readonly string WebApp = Path.Combine(RepositoryRoot, "src", "Web", "procure-ai-web");

    private static string Nvmrc => File.ReadAllText(Path.Combine(RepositoryRoot, ".nvmrc")).Trim();

    [Fact]
    public void Nvmrc_matches_dockerfile()
    {
        Nvmrc.ShouldMatch(@"^\d+\.\d+\.\d+$", ".nvmrc pins an exact patch");
        var from = FromNode().Matches(File.ReadAllText(Path.Combine(WebApp, "Dockerfile")));

        from.Count.ShouldBeGreaterThan(0, "the web Dockerfile builds on node:<version>");
        from.Select(m => m.Groups["version"].Value).ShouldAllBe(v => v == Nvmrc, $"every FROM node: in the web Dockerfile uses .nvmrc's {Nvmrc}");
    }

    [Fact]
    public void Engines_allow_the_nvmrc_version_and_npm_enforces_them()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(WebApp, "package.json")));
        var range = package.RootElement.GetProperty("engines").GetProperty("node").GetString()!;
        var bounds = EnginesRange().Match(range);
        bounds.Success.ShouldBeTrue($"engines.node is \">=X.Y.Z <N\" (was \"{range}\")");

        var version = Version.Parse(Nvmrc);
        version.ShouldBeGreaterThanOrEqualTo(Version.Parse(bounds.Groups["min"].Value), $"engines.node {range} allows .nvmrc's {Nvmrc}");
        version.Major.ShouldBeLessThan(int.Parse(bounds.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture), $"engines.node {range} allows .nvmrc's {Nvmrc}");

        var npmrc = Path.Combine(WebApp, ".npmrc");
        File.Exists(npmrc).ShouldBeTrue("src/Web/procure-ai-web/.npmrc sets engine-strict");
        File.ReadAllLines(npmrc).Select(l => l.Trim()).ShouldContain("engine-strict=true");
    }

    [GeneratedRegex(@"^FROM\s+node:(?<version>[0-9.]+)(-[a-z0-9.]+)?(\s|$)", RegexOptions.Multiline)]
    private static partial Regex FromNode();

    [GeneratedRegex(@"^>=(?<min>\d+\.\d+\.\d+)\s+<(?<major>\d+)$")]
    private static partial Regex EnginesRange();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProcureAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ProcureAi.slnx not found above " + AppContext.BaseDirectory);
    }
}
