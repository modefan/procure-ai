using Microsoft.AspNetCore.TestHost;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>Swaps the process-wide Console.Out, so it must never run in parallel with other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutCollection
{
    public const string Name = "Console.Out swapping tests";
}

[Collection(ConsoleOutCollection.Name)]
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
