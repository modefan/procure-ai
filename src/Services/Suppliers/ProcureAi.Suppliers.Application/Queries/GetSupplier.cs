using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Suppliers.Application.Abstractions;
using ProcureAi.Suppliers.Application.Dtos;
using ProcureAi.Suppliers.Application.Mapping;
using ProcureAi.Suppliers.Domain;

namespace ProcureAi.Suppliers.Application.Queries;

public sealed record GetSupplier(string Code);

public static class GetSupplierHandler
{
    public static async Task<SupplierDto> Handle(GetSupplier query, ISupplierStore store, CancellationToken cancellationToken) =>
        SupplierMapper.ToDto(await SupplierLookup.RequireAsync(store, query.Code, cancellationToken));
}

internal static class SupplierLookup
{
    /// <summary>A supplier of another tenant is not found either: the store only sees the caller's tenant.</summary>
    public static async Task<Supplier> RequireAsync(ISupplierStore store, string code, CancellationToken cancellationToken) =>
        await store.GetAsync(code, cancellationToken) ?? throw new NotFoundException($"No supplier {code}.");
}
