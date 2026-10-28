using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>
/// Makes chosen saves fail once with a transient <see cref="NpgsqlException"/> (a connection reset while writing), as a database
/// blip does. A test arms it with a condition on the saving DbContext (e.g. "a spend of payment X is being added"); the first save
/// that matches throws and disarms it, every other save goes through. Shared by every instance of the API in the collection.
/// </summary>
public sealed class TransientDatabaseFault : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<Guid, Func<DbContext, bool>> _armed = new();
    private int _thrown;

    /// <summary>How many saves failed because of this fault.</summary>
    public int Thrown => _thrown;

    /// <summary>The next save of a DbContext for which <paramref name="when"/> is true fails once.</summary>
    public void FailOnce(Func<DbContext, bool> when) => _armed[Guid.NewGuid()] = when;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Fail(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Fail(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Fail(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var (key, when) in _armed)
        {
            if (when(context) && _armed.TryRemove(key, out _))
            {
                Interlocked.Increment(ref _thrown);
                var fault = new NpgsqlException("Exception while writing to stream", new IOException("Connection reset by peer"));
                if (!fault.IsTransient)
                {
                    throw new InvalidOperationException("The simulated fault must be one Npgsql calls transient.");
                }

                throw fault;
            }
        }
    }
}
