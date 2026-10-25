using Microsoft.AspNetCore.TestHost;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>
/// Tests that touch process-wide state (Console.Out, the static Serilog Log.Logger that every web host replaces)
/// and so must never run in parallel with other tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    public const string Name = "Process-wide state tests";
}

[Collection(GlobalStateCollection.Name)]
public class ConsoleFallbackTests
{
    [Fact]
    public async Task Console_sink_is_used_when_there_is_no_serilog_section()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await using var app = await TestHost.StartAsync();
            (await app.GetTestClient().GetAsync("/api/v1/test/log", ct)).EnsureSuccessStatusCode();
        }
        finally
        {
            Console.SetOut(original);
        }

        captured.ToString().ShouldContain("warning-event");
    }
}
