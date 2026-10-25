using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

public sealed record StartChain(Guid Marker);

public sealed record ContinueChain(Guid Marker);

public static class ChainHandlers
{
    public static ConcurrentDictionary<Guid, (string UserId, string ActorId, string? TenantId, IReadOnlyDictionary<string, string?> Headers)> Seen { get; } = new();

    public static ContinueChain Handle(StartChain message) => new(message.Marker);

    public static void Handle(ContinueChain message, ICallerContext caller, Envelope envelope) =>
        Seen[message.Marker] = (caller.UserId, caller.ActorId, caller.TenantId, new Dictionary<string, string?>(envelope.Headers));
}

public sealed class CallerPropagationTests
{
    [Fact]
    public async Task Message_sent_while_handling_a_message_keeps_initiating_user_not_actor()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddProcureAiCaller("test-service"))
            .UseWolverine(options =>
            {
                options.AddCallerPropagation();
                options.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ChainHandlers));
            })
            .StartAsync(TestContext.Current.CancellationToken);
        var marker = Guid.NewGuid();
        var delivery = new DeliveryOptions();
        delivery.Headers[CallerHeaders.UserId] = "tom";
        delivery.Headers[CallerHeaders.TenantId] = "acme";

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(c => c.SendAsync(new StartChain(marker), delivery));

        var seen = ChainHandlers.Seen[marker];
        // The second message was sent by the first handler (acting as the service) but is still for tom.
        seen.Headers[CallerHeaders.UserId].ShouldBe("tom");
        seen.Headers[CallerHeaders.TenantId].ShouldBe("acme");
        seen.Headers.Values.ShouldNotContain(v => v != null && v.Contains("service:", StringComparison.Ordinal));
        seen.UserId.ShouldBe("tom");
        seen.TenantId.ShouldBe("acme");
        seen.ActorId.ShouldBe("service:test-service");
    }
}
