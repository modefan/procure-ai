using System.Diagnostics;
using System.Reflection;
using Shouldly;

namespace ProcureAi.ArchitectureTests;

/// <summary>
/// The pre-generated Wolverine code (plan 07 Task 5b, docs/generated-code.md). The images run <c>Wolverine:TypeLoadMode=Static</c>:
/// a handler whose code is missing fails the service at startup, and code that no longer matches the handler runs the old
/// pipeline. So every service API's committed <c>Internal/Generated</c> must equal what <c>codegen write</c> produces now: the test
/// writes a fresh copy (the <c>WriteGeneratedCode</c> target with <c>GeneratedCodeDirectory</c> redirected) and compares file by
/// file. When it fails, regenerate: <c>dotnet build src/Services/&lt;S&gt;/ProcureAi.&lt;S&gt;.Api -t:Build;WriteGeneratedCode</c>.
/// </summary>
public sealed class GeneratedCodeTests
{
    public static TheoryData<string> ServiceApis()
    {
        var data = new TheoryData<string>();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", "Services"), "ProcureAi.*.Api.csproj", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(RepositoryRoot, project).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ServiceApis))]
    public void Committed_generated_code_matches_what_codegen_writes(string project)
    {
        var committed = Path.Combine(Path.GetDirectoryName(Path.Combine(RepositoryRoot, project))!, "Internal", "Generated");
        Directory.Exists(committed).ShouldBeTrue($"{project} has no committed Internal/Generated; run its WriteGeneratedCode target");

        var fresh = Path.Combine(Path.GetTempPath(), "procureai-codegen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exitCode, output) = Run("dotnet", "msbuild", Path.Combine(RepositoryRoot, project), "-t:WriteGeneratedCode",
                $"-p:GeneratedCodeDirectory={fresh}", $"-p:Configuration={BuildConfiguration}", "-nologo", "-v:q");
            exitCode.ShouldBe(0, output);

            var expected = Files(fresh);
            var actual = Files(committed);
            expected.Keys.ShouldNotBeEmpty($"codegen wrote nothing for {project}");
            actual.Keys.Order(StringComparer.Ordinal).ShouldBe(expected.Keys.Order(StringComparer.Ordinal),
                $"generated files of {project} (missing or stale: regenerate with -t:Build;WriteGeneratedCode)");
            foreach (var (file, content) in expected)
            {
                actual[file].ShouldBe(content, $"{project}: Internal/Generated/{file} is stale; regenerate with -t:Build;WriteGeneratedCode");
            }
        }
        finally
        {
            if (Directory.Exists(fresh))
            {
                Directory.Delete(fresh, recursive: true);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ServiceApis))]
    public void The_image_runs_the_pre_generated_code(string project)
    {
        var (exitCode, output) = Run("dotnet", "msbuild", Path.Combine(RepositoryRoot, project),
            "-getItem:ContainerEnvironmentVariable", "-nologo");
        exitCode.ShouldBe(0, output);
        output.ShouldContain("\"Identity\": \"Wolverine__TypeLoadMode\"");
        output.ShouldContain("\"Value\": \"Static\"");
    }

    private static Dictionary<string, string> Files(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(root, file).Replace('\\', '/'),
            file => File.ReadAllText(file).ReplaceLineEndings("\n"),
            StringComparer.Ordinal);

    private static (int ExitCode, string Output) Run(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepositoryRoot };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(TimeSpan.FromMinutes(3)).ShouldBeTrue($"{fileName} {string.Join(' ', arguments)} did not finish");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string BuildConfiguration =>
        typeof(GeneratedCodeTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    private static readonly string RepositoryRoot = FindRepositoryRoot();

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
