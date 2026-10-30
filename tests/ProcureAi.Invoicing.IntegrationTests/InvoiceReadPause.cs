using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProcureAi.Invoicing.IntegrationTests;

/// <summary>
/// Forces an interleaving in a payment command: a request sent with the <see cref="Header"/> header stops before its second read of
/// the rows the payment rules depend on (the invoice and its order view), until the test lets it go. Another payment can commit
/// in between, which is the moment a real race hits only now and then.
/// </summary>
public sealed partial class InvoiceReadPause : DbCommandInterceptor
{
    public const string Header = "X-Test-Pause-Second-Read";

    private static readonly object CountKey = new();

    private readonly HttpContextAccessor _http = new();
    private TaskCompletionSource _reached = NewSource();
    private TaskCompletionSource _release = NewSource();

    /// <summary>Completes when the paused request is waiting before its second read.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Starts a new pause (one per test).</summary>
    public void Arm()
    {
        _reached = NewSource();
        _release = NewSource();
    }

    /// <summary>Lets the paused request go on.</summary>
    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (IsSecondRead(command))
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        return result;
    }

    private bool IsSecondRead(DbCommand command)
    {
        if (_http.HttpContext is not { } context || !context.Request.Headers.ContainsKey(Header)
            || !PaymentRows().IsMatch(command.CommandText))
        {
            return false;
        }

        var count = context.Items.TryGetValue(CountKey, out var seen) ? (int)seen! + 1 : 1;
        context.Items[CountKey] = count;
        return count == 2;
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [GeneratedRegex("""FROM "?(invoices|order_views)"? """)]
    private static partial Regex PaymentRows();
}
