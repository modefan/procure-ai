using System.Diagnostics;

namespace ProcureAi.Testing;

/// <summary>
/// Runs the real <c>&lt;Service&gt;.Api --migrate</c> (what the deployment's migrate step does), so migration, message store and demo
/// seed are exercised as deployed. The test project must reference the Api project (its dll is next to the test assembly).
/// </summary>
public static class ApiMigrateProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <param name="apiAssembly">File name of the Api assembly, e.g. <c>ProcureAi.Budgets.Api.dll</c>.</param>
    /// <param name="databaseConnectionName">The service's connection-string name, e.g. <c>budgetsdb</c>.</param>
    /// <param name="contentRoot">
    /// Where the Api reads its <c>appsettings.json</c>, relative to the repository root (e.g. <c>src/Services/Audit/ProcureAi.Audit.Api</c>).
    /// Needed when the test project references several Api projects: their <c>appsettings.json</c> files all land in the test
    /// output folder, and only one of them survives there. Default: the test output folder.
    /// </param>
    /// <param name="environment">More settings as environment variables (e.g. <c>ConnectionStrings__assistantchatdb</c>, a second database).</param>
    public static async Task RunAsync(
        string apiAssembly, string databaseConnectionName, string database, string rabbit, string redis, bool demoSeed,
        string? contentRoot = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, apiAssembly);
        var start = new ProcessStartInfo("dotnet", ["exec", dll, "--migrate"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        // Production settings: no automatic schema changes, so any table that appears came from --migrate.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment[$"ConnectionStrings__{databaseConnectionName}"] = database;
        start.Environment["ConnectionStrings__rabbitmq"] = rabbit;
        start.Environment["ConnectionStrings__Redis"] = redis;
        start.Environment["DemoSeed__Enabled"] = demoSeed ? "true" : "false";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }
        if (contentRoot is not null)
        {
            // The environment variable, not --contentRoot: WebApplication.CreateBuilder ignores the command-line content root for appsettings.
            start.Environment["ASPNETCORE_CONTENTROOT"] = Path.GetFullPath(Path.Combine(PostgresFixture.RepoRoot(), contentRoot));
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // A hung migration must not outlive the test run (it would hold database connections and file locks): kill it and
            // everything it started.
            string killed = "and was killed";
            var exited = true;
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                // The timeout is the failure to report; the kill failure travels with it and is not hidden.
                killed = $"and could not be killed ({exception.GetType().Name}: {exception.Message}); check for a leftover process";
                exited = false;
            }

            // A process that is still alive never closes its output, so only read it once the process has exited.
            var captured = exited ? (await output) + (await error) : "(output not read: the process may still be running)";
            throw new TimeoutException($"{apiAssembly} --migrate did not finish within {Timeout} {killed}: {captured}");
        }

        var text = (await output) + (await error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{apiAssembly} --migrate exited with {process.ExitCode}: {text}");
        }
    }
}
