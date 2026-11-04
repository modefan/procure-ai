using System.Text.Json;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Audit.Application.Queries;

/// <summary>Who raised a request and on which cost center: what its caller scope is decided on.</summary>
public sealed record RequestOwner(string RequesterId, string CostCenterCode);

/// <summary>
/// The Requests read rule (plan 07 Task 0c ruling, Task 5c) applied to Audit's per-request reads and the activity feed: the admin key
/// and a service acting for nobody (<see cref="CallerScope.All"/>) and a tenant-admin user (<c>wholeTenant</c>, decided by the Api)
/// read every request of the tenant; anyone else only those they raised or of one of their cost centers (<see cref="CallerScope.Allows"/>).
/// The owner comes from what Audit stores: the request's <c>RequestSubmitted</c> (a submitted request's cost center no longer changes),
/// else, for a draft, the newest captured row of the requests table. When neither is there the request is out of scope: the read
/// fails closed (404), it never falls back to the tenant.
/// </summary>
public static class RequestReadScope
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Throws <see cref="NotFoundException"/> (<paramref name="notFound"/>) unless the caller may read the request.</summary>
    public static async Task EnsureReadableAsync(
        Guid requestId, string tenant, bool wholeTenant, ICallerScope scope, IAuditStore store, IDataChangeStore changes,
        RequestScopeOptions options, string notFound, CancellationToken cancellationToken)
    {
        var callerScope = await scope.GetAsync(cancellationToken);
        if (callerScope.All || wholeTenant)
        {
            return;
        }

        var submitted = await store.SubmissionsAsync(tenant, [requestId], cancellationToken);
        var owner = submitted.Select(FromSubmission).FirstOrDefault(o => o is not null)
            ?? FromRow(await changes.LatestRowOfSubjectAsync(tenant, options.Database, options.Table, requestId, cancellationToken));
        if (owner is null || !callerScope.Allows(owner.RequesterId, owner.CostCenterCode))
        {
            throw new NotFoundException(notFound);
        }
    }

    /// <summary>The requester and cost center a <c>RequestSubmitted</c> event names; null when it cannot be read.</summary>
    public static RequestOwner? FromSubmission(BusinessEvent submitted)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<RequestSubmitted>(submitted.Payload, Json);
            return payload is { RequesterId: { Length: > 0 } requester, CostCenterCode: { Length: > 0 } costCenter }
                ? new RequestOwner(requester, costCenter)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>requester_id</c> and <c>cost_center_code</c> of a captured requests row (after the change, else before); null when missing.</summary>
    public static RequestOwner? FromRow(DataChange? change)
    {
        if ((change?.After ?? change?.Before) is not { } row)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(row);
            return Text(document.RootElement, "requester_id") is { } requester && Text(document.RootElement, "cost_center_code") is { } costCenter
                ? new RequestOwner(requester, costCenter)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement row, string column) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
