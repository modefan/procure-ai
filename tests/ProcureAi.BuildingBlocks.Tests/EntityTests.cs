using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests;

public class EntityTests
{
    [Fact]
    public void Entity_collects_and_clears_domain_events()
    {
        var e = new TestEntity();
        e.Do();
        e.DomainEvents.Count.ShouldBe(1);
        e.ClearDomainEvents();
        e.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Domain_events_cannot_be_mutated_through_the_exposed_list()
    {
        var e = new TestEntity();
        e.Do();
        (e.DomainEvents is List<object>).ShouldBeFalse();
    }

    private sealed class TestEntity : Entity<Guid>
    {
        public void Do() => Raise("happened");
    }
}
