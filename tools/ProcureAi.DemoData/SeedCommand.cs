using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ProcureAi.DemoData;

/// <summary><c>seed</c>: every scenario file, the tenants at the same time. 0 when every request reached its stop, else 1.</summary>
public sealed class SeedCommand(IServiceProvider services)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILogger<SeedCommand>>();
        var options = services.GetRequiredService<IOptions<DemoDataOptions>>().Value;
        IReadOnlyList<Scenario> scenarios;
        try
        {
            scenarios = Scenarios.Load(options.ScenarioPath);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            logger.LogError("The scenario files are invalid: {Message}", e.Message);
            return 1;
        }

        var clock = Stopwatch.StartNew();
        var runner = services.GetRequiredService<ScenarioRunner>();
        var results = await Task.WhenAll(scenarios.Select(s => runner.RunAsync(s, cancellationToken)));
        var failures = results.SelectMany(r => r.Failures).ToList();
        foreach (var failure in failures)
        {
            logger.LogError("Seed failed: {Message}", failure.Message);
        }

        logger.LogInformation(
            "Demo data seeded in {Seconds:0} s: {Summary}{Failed}",
            clock.Elapsed.TotalSeconds,
            string.Join(", ", results.Select(r => $"{r.Tenant} {r.Created} created, {r.Found} already there")),
            failures.Count == 0 ? "" : $"; {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}
