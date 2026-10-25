using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests;

public sealed class OptimisticConcurrencyTests
{
    [Fact]
    public void Matching_version_passes()
    {
        Should.NotThrow(() => OptimisticConcurrency.EnsureVersion("Request 1", 7, 7));
    }

    [Fact]
    public void Stale_version_is_a_concurrency_conflict_naming_both_versions()
    {
        var error = Should.Throw<ConcurrencyConflictException>(() => OptimisticConcurrency.EnsureVersion("Request 1", 8, 7));

        error.Message.ShouldBe("Request 1 was changed by someone else (version 8, expected 7).");
    }

    [Fact]
    public async Task Lost_race_on_save_is_a_concurrency_conflict()
    {
        var error = await Should.ThrowAsync<ConcurrencyConflictException>(() => OptimisticConcurrency.SaveOrConflictAsync(
            _ => throw new DbUpdateConcurrencyException("row changed"), "The user", CancellationToken.None));

        error.Message.ShouldStartWith("The user was changed by someone else.");
    }

    [Fact]
    public async Task Successful_save_and_other_errors_pass_through()
    {
        var saved = 0;
        await OptimisticConcurrency.SaveOrConflictAsync(_ => Task.FromResult(++saved), "The user", CancellationToken.None);
        saved.ShouldBe(1);

        await Should.ThrowAsync<InvalidOperationException>(() => OptimisticConcurrency.SaveOrConflictAsync(
            _ => throw new InvalidOperationException("boom"), "The user", CancellationToken.None));
    }
}
