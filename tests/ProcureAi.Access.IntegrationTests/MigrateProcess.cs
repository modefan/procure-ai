using System.Diagnostics;
using Shouldly;

namespace ProcureAi.Access.IntegrationTests;

/// <summary>Runs the real <c>ProcureAi.Access.Api --migrate</c> (what the deployment's migrate step does), so the seed hook is exercised as deployed.</summary>
public static class MigrateProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static async Task RunAsync(string database, string rabbit, string redis, bool demoSeed)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ProcureAi.Access.Api.dll");
        var start = new ProcessStartInfo("dotnet", ["exec", dll, "--migrate"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        // Production settings: no automatic schema changes, so any table that appears came from --migrate.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__accessdb"] = database;
        start.Environment["ConnectionStrings__rabbitmq"] = rabbit;
        start.Environment["ConnectionStrings__Redis"] = redis;
        start.Environment["DemoSeed__Enabled"] = demoSeed ? "true" : "false";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        await process.WaitForExitAsync(timeout.Token);
        process.ExitCode.ShouldBe(0, (await output) + (await error));
    }
}
