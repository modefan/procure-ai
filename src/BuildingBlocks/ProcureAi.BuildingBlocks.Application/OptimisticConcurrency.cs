using Microsoft.EntityFrameworkCore;

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// The If-Match rule every service applies to a versioned entity (the Postgres xmin column): a stale version is a
/// <see cref="ConcurrencyConflictException"/> (HTTP 412), whether it is seen on load or lost in a race on save.
/// </summary>
public static class OptimisticConcurrency
{
    /// <summary>Throws when the version the caller last saw is not the current one. <paramref name="what"/> names the entity, e.g. <c>Request 5</c>.</summary>
    public static void EnsureVersion(string what, uint actual, uint expected)
    {
        if (actual != expected)
        {
            throw new ConcurrencyConflictException($"{what} was changed by someone else (version {actual}, expected {expected}).");
        }
    }

    /// <summary>Runs <paramref name="save"/> (the DbContext SaveChangesAsync); a write that lost a race on the version column becomes a <see cref="ConcurrencyConflictException"/>.</summary>
    public static async Task SaveOrConflictAsync(Func<CancellationToken, Task<int>> save, string what, CancellationToken cancellationToken)
    {
        try
        {
            await save(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException($"{what} was changed by someone else. {exception.Message}");
        }
    }
}
