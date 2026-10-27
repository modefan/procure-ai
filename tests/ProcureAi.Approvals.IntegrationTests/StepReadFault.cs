using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProcureAi.Approvals.IntegrationTests;

/// <summary>
/// A database blip on demand: while armed, the n-th read of a single approval step (what <c>GetApprovalStep</c> runs) fails. The
/// API reads the step before an approval and again after it committed, so <c>FailNthRead(2)</c> breaks only the read after the commit.
/// </summary>
public sealed class StepReadFault : DbCommandInterceptor
{
    private int _remaining;

    public void FailNthRead(int n) => Volatile.Write(ref _remaining, n);

    public void Disarm() => Volatile.Write(ref _remaining, 0);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command);
        return result;
    }

    private void Check(DbCommand command)
    {
        if (!command.CommandText.Contains("approval_steps", StringComparison.Ordinal)
            || !command.CommandText.Contains("LIMIT 2", StringComparison.Ordinal))
        {
            return;
        }

        if (Volatile.Read(ref _remaining) > 0 && Interlocked.Decrement(ref _remaining) == 0)
        {
            throw new TimeoutException("Simulated database blip after the commit.");
        }
    }
}
