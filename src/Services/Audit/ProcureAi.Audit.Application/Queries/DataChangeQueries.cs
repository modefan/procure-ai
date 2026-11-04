using System.Text.Json;
using Microsoft.Extensions.Options;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Cdc;
using ProcureAi.Audit.Application.Dtos;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Audit.Application.Queries;

/// <summary>
/// The data changes of one purchase request of the caller tenant, across services, oldest first (screen 7, "Data changes"), for
/// callers who may read the request (<see cref="RequestReadScope"/>; <paramref name="WholeTenant"/> is decided by the Api).
/// </summary>
public sealed record GetRequestDataChanges(Guid RequestId, int Page, int PageSize, bool WholeTenant = false);

/// <summary>
/// The data-change log (screen 24). A user sees the changes of their own tenant; the admin API key without a tenant sees every
/// tenant and the changes no tenant can be told for (a table without <c>tenant_id</c> changed outside the app).
/// </summary>
public sealed record SearchDataChanges(
    string? Database, string? Table, string? UserId, bool OutsideOnly, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize);

public static class DataChangeQueriesHandler
{
    /// <summary>
    /// 404 when the tenant has no change about the request (another tenant's request looks the same as none) and when the request is
    /// outside the caller's scope.
    /// </summary>
    public static async Task<PagedResult<DataChangeDto>> Handle(
        GetRequestDataChanges query, ICallerContext caller, ICallerScope scope, IDataChangeStore store, IAuditStore events,
        IOptions<AuditOptions> options, CancellationToken cancellationToken)
    {
        var notFound = $"No data changes for request {query.RequestId}.";
        var tenant = caller.TenantId ?? throw new NotFoundException(notFound);
        await RequestReadScope.EnsureReadableAsync(
            query.RequestId, tenant, query.WholeTenant, scope, events, store, options.Value.RequestScope, notFound, cancellationToken);
        var page = await store.ForSubjectAsync(tenant, query.RequestId, query.Page, query.PageSize, cancellationToken);
        if (page.Total == 0)
        {
            throw new NotFoundException(notFound);
        }

        return DataChangeView.ToDto(page);
    }

    public static async Task<PagedResult<DataChangeDto>> Handle(
        SearchDataChanges query, ICallerContext caller, IDataChangeStore store,
        CancellationToken cancellationToken)
    {
        if (caller.TenantId is null && caller.Kind != CallerKind.AdminApiKey)
        {
            throw new ForbiddenException("The data-change log of every tenant is only for the platform (admin API key).");
        }

        var filter = new DataChangeFilter(
            caller.TenantId, Blank(query.Database), Blank(query.Table), Blank(query.UserId), query.OutsideOnly, query.From, query.To);
        return DataChangeView.ToDto(await store.SearchAsync(filter, query.Page, query.PageSize, cancellationToken));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>A stored change as the screens show it: who and when, and each column's old and new value.</summary>
public static class DataChangeView
{
    public static PagedResult<DataChangeDto> ToDto(PagedResult<DataChange> page) =>
        new([.. page.Items.Select(ToDto)], page.Page, page.PageSize, page.Total);

    public static DataChangeDto ToDto(DataChange change) => new(
        change.Id, change.Database, change.Table, change.PrimaryKey, change.SubjectId, change.TenantId, change.Operation,
        change.TransactionId, change.CommittedAt, change.Attribution.Kind, change.Attribution.UserId, change.Attribution.ActorId,
        change.Attribution.CallingService, change.Attribution.CorrelationId, Fields(change.Before, change.After));

    /// <summary>
    /// The columns that differ between the two images (an insert: every column of the new row; a delete: every column of the old
    /// one). Values as text (<see cref="DebeziumChange.Text(JsonElement)"/>).
    /// </summary>
    public static IReadOnlyList<FieldChangeDto> Fields(string? before, string? after)
    {
        var old = Columns(before);
        var @new = Columns(after);
        var names = @new.Keys.Concat(old.Keys.Where(k => !@new.ContainsKey(k)));
        return [.. names
            .Select(name => new FieldChangeDto(name, old.GetValueOrDefault(name), @new.GetValueOrDefault(name)))
            .Where(field => before is null || after is null || field.Old != field.New)];
    }

    private static Dictionary<string, string?> Columns(string? json)
    {
        var columns = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (json is null)
        {
            return columns;
        }

        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            columns[property.Name] = DebeziumChange.Text(property.Value);
        }

        return columns;
    }
}
