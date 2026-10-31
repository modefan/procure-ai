using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProcureAi.DemoData;

/// <summary>
/// <c>reset</c> (plan 06 ruling 4): every service's business data is cleared (<see cref="ResetRunner"/>), then <c>seed</c> writes the demo
/// data again. 0 when both succeeded; 1 when the reset or the seed failed (a failed reset does not seed). Safe to run again after a failure.
/// </summary>
public sealed class ResetCommand(IServiceProvider services)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<ResetCommand>>();
        var clock = Stopwatch.StartNew();
        if (!await services.GetRequiredService<ResetRunner>().RunAsync(cancellationToken))
        {
            logger.LogError("Demo reset failed after {Seconds:0} s; nothing was seeded. Run reset again: it starts from the beginning", clock.Elapsed.TotalSeconds);
            return 1;
        }

        var cleared = clock.Elapsed;
        var exitCode = await new SeedCommand(services).RunAsync(cancellationToken);
        logger.LogInformation(
            "Demo reset and seed took {Seconds:0} s (reset {ResetSeconds:0} s, seed {SeedSeconds:0} s), exit code {ExitCode}",
            clock.Elapsed.TotalSeconds, cleared.TotalSeconds, (clock.Elapsed - cleared).TotalSeconds, exitCode);
        return exitCode;
    }
}

/// <summary>
/// <c>schedule</c>: sleeps until the next <c>DemoReset:TimeOfDay</c> in <c>DemoReset:TimeZone</c> (02:00 Africa/Johannesburg), runs
/// <c>reset</c> (which seeds), logs how long it took, and waits for the next night. A failed night is logged and the loop goes on. Runs until
/// stopped (0). Plan 07 runs <c>reset</c> as a Kubernetes CronJob instead.
/// </summary>
public sealed class ScheduleCommand(IServiceProvider services, DemoResetScheduleOptions schedule)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<ScheduleCommand>>();
        var time = services.GetRequiredService<TimeProvider>();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = DemoSchedule.NextRun(time.GetUtcNow(), schedule.TimeOfDay, zone);
                logger.LogInformation("Next demo reset at {Next:yyyy-MM-dd HH:mm zzz} ({Zone})", next, schedule.TimeZone);
                var wait = next - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, cancellationToken);
                }

                var clock = Stopwatch.StartNew();
                var exitCode = await new ResetCommand(services).RunAsync(cancellationToken);
                logger.Log(
                    exitCode == 0 ? LogLevel.Information : LogLevel.Error,
                    "Nightly demo reset finished in {Seconds:0} s with exit code {ExitCode}", clock.Elapsed.TotalSeconds, exitCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }

        return 0;
    }
}
