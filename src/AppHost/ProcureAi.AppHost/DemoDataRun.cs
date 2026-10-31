using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureAi.AppHost;

/// <summary>
/// Which mode the <c>demo-data</c> resource runs next (plan 06 ruling 4): <c>seed</c> on every start, except the one start the dashboard
/// command "Reset demo data" asks for, which runs <c>reset</c> (every service cleared, then seeded). The command starts the resource again
/// (it has exited after its seed) or restarts it when it is still running.
/// </summary>
public sealed class DemoDataRun
{
    public const string ResetCommand = "reset-demo-data";

    private int _resetRequested;

    /// <summary>The mode for this start; a requested reset is used once.</summary>
    public string Next() => Interlocked.Exchange(ref _resetRequested, 0) == 1 ? "reset" : "seed";

    public async Task<ExecuteCommandResult> ResetAsync(ExecuteCommandContext context)
    {
        var model = context.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.Single(r => r.Name == ResourceNames.DemoData);
        var commands = context.Services.GetRequiredService<ResourceCommandService>();

        Interlocked.Exchange(ref _resetRequested, 1);
        var started = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand, context.CancellationToken);
        if (!started.Success)
        {
            // Still running (a seed in progress): restart it, so the reset starts from the beginning.
            started = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.RestartCommand, context.CancellationToken);
        }

        if (!started.Success)
        {
            Interlocked.Exchange(ref _resetRequested, 0);
            return CommandResults.Failure(started.Message ?? "demo-data could not be started.");
        }

        return CommandResults.Success();
    }
}
