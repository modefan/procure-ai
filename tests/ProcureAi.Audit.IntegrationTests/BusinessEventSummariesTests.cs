using ProcureAi.Audit.Application.Queries;
using ProcureAi.Audit.Domain;
using ProcureAi.Contracts;
using Shouldly;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>The readable history: every integration event type has its own sentence (no container needed).</summary>
public sealed class BusinessEventSummariesTests
{
    private static readonly Type[] EventTypes = typeof(IIntegrationEvent).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IIntegrationEvent).IsAssignableFrom(t))
        .ToArray();

    [Fact]
    public void Every_integration_event_type_has_a_summary()
    {
        EventTypes.Length.ShouldBeGreaterThan(10, "the contracts were found by reflection");
        var summaries = BusinessEventSummaries.Read([]);

        foreach (var type in EventTypes)
        {
            var contract = (IIntegrationEvent)ContractSamples.Of(type, "acme", Guid.NewGuid(), "user-1");
            var stored = new BusinessEvent(
                Guid.NewGuid(), "acme", type.Name, null, "user-1", null, "test", contract.OccurredAt, "{}");

            var sentence = summaries.Summarize(new ReadEvent(stored, contract), new Dictionary<string, string>());

            sentence.ShouldNotBe(type.Name, $"{type.Name} falls back to its type name: add a sentence to BusinessEventSummaries");
            sentence.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
