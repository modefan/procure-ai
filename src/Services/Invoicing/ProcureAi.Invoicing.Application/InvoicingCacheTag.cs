using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Invoicing.Application;

/// <summary>
/// The tags of the cached invoice reads: one invoice, and the tenant list. The API reads use the templates in <c>[CachedGet]</c>;
/// controllers after a write and handlers after their commit evict the formatted tags.
/// </summary>
public static class InvoicingCacheTag
{
    /// <summary>The route value is <c>id</c>.</summary>
    public const string ItemTemplate = "invoicing:{tenant}:{id}";

    /// <summary>Every list of the tenant.</summary>
    public const string ListTemplate = "invoicing:{tenant}:invoices";

    /// <summary>Both tags a change of one invoice touches.</summary>
    public static string[] Of(string? tenantId, Guid invoiceId) => Of(tenantId, [invoiceId]);

    /// <summary>The tags of several invoices at once (a goods receipt can change the outcome of many), and the list.</summary>
    public static string[] Of(string? tenantId, IEnumerable<Guid> invoiceIds) =>
    [
        .. invoiceIds.Select(id => CacheTag.Format(ItemTemplate, CacheTag.Tenant(tenantId), ("id", id))!).Distinct(),
        CacheTag.Format(ListTemplate, CacheTag.Tenant(tenantId))!,
    ];
}
