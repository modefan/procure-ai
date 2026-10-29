namespace ProcureAi.Assistant.Application.Tools;

/// <summary>A supplier of the catalog and how many items it offers.</summary>
public sealed record SupplierInfo(string Code, string Name, bool Active, int ItemCount);

/// <summary>The Suppliers service, called as the signed-in user (its permissions and tenant).</summary>
public interface ISuppliersLookup
{
    /// <summary><c>GET api/v1/suppliers?search=&amp;page=1&amp;pageSize=</c>: suppliers whose code or name contains the text.</summary>
    Task<IReadOnlyList<SupplierInfo>> SearchAsync(string search, int pageSize, CancellationToken cancellationToken);
}
