using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Application.Abstractions;

/// <summary>
/// Read side of the supplier catalog, always for the caller's tenant (Infrastructure enforces it). The catalog has no write API:
/// it is loaded by the migrate step's seed.
/// </summary>
public interface ISupplierStore
{
    /// <summary>One page of the tenant's suppliers by code, optionally narrowed to those whose code or name contains <paramref name="search"/> (case-insensitive).</summary>
    Task<(IReadOnlyList<Supplier> Items, int Total)> ListAsync(string? search, int skip, int take, CancellationToken cancellationToken);

    Task<Supplier?> GetAsync(string code, CancellationToken cancellationToken);
}
