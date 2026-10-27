using JasperFx;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;
using System.Reflection;
using Wolverine;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

public sealed class ConcurrencyConflictTests
{
    [Fact]
    public void Lost_races_in_the_database_are_concurrency_conflicts()
    {
        ConcurrencyConflicts.IsLostRace(new DbUpdateConcurrencyException("row changed")).ShouldBeTrue();
        ConcurrencyConflicts.IsLostRace(new ConcurrencyException("saga changed")).ShouldBeTrue();
        ConcurrencyConflicts.IsLostRace(new DbUpdateException("dup", new PostgresException("dup", "ERROR", "ERROR", "23505"))).ShouldBeTrue();
    }

    [Fact]
    public void Answers_and_other_failures_are_not()
    {
        ConcurrencyConflicts.IsLostRace(new ConcurrencyConflictException("stale If-Match")).ShouldBeFalse();
        ConcurrencyConflicts.IsLostRace(new ConflictException("not pending")).ShouldBeFalse();
        ConcurrencyConflicts.IsLostRace(new DbUpdateException("fk", new PostgresException("fk", "ERROR", "ERROR", "23503"))).ShouldBeFalse();
        ConcurrencyConflicts.IsLostRace(new InvalidOperationException("bug")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_race_still_lost_after_the_retries_is_a_409()
    {
        var bus = Bus(new DbUpdateConcurrencyException("row changed again"));

        var exception = await Should.ThrowAsync<ConflictException>(
            bus.InvokeOrConflictAsync(new object(), "The approval", TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("The approval was changed by someone else at the same time. Reload it and try again.");
    }

    [Fact]
    public async Task Other_failures_pass_through_invoke_or_conflict()
    {
        var bus = Bus(new InvalidOperationException("bug"));

        await Should.ThrowAsync<InvalidOperationException>(
            bus.InvokeOrConflictAsync(new object(), "The approval", TestContext.Current.CancellationToken));
    }

    /// <summary>A bus whose in-process invoke throws <paramref name="failure"/> (what is left after Wolverine's retries are used up).</summary>
    private static IMessageBus Bus(Exception failure)
    {
        var bus = DispatchProxy.Create<IMessageBus, ThrowingBus>();
        ((ThrowingBus)(object)bus).Failure = failure;
        return bus;
    }

    public class ThrowingBus : DispatchProxy
    {
        public Exception Failure { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IMessageBus.InvokeAsync) ? Task.FromException(Failure) : throw new NotSupportedException(targetMethod.Name);
    }
}
