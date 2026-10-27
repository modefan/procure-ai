using JasperFx;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;

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
}
