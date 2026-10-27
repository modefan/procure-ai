using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Commands;

/// <summary>One line of a create or update command: a catalog SKU and a quantity. Description and price come from the supplier's catalog.</summary>
public sealed record LineInput(string Sku, int Quantity);

public sealed record CreateRequest(string Title, string CostCenterCode, string SupplierId, IReadOnlyList<LineInput> Lines);

public static class CreateRequestHandler
{
    public static async Task<RequestDto> Handle(
        CreateRequest command,
        ICallerContext caller,
        ICallerScope scope,
        IRequestsDbContext db,
        IRequestNumberGenerator numbers,
        ISupplierCatalog catalog,
        CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new DomainException("A tenant is required.");
        await CostCenterOwnership.EnsureAsync(scope, command.CostCenterCode, cancellationToken);
        // Prices first: an unknown supplier or SKU is refused before a request number is used.
        var prices = await LinePricing.LookUpAsync(
            catalog, command.SupplierId, [.. command.Lines.Select(l => l.Sku).Distinct(StringComparer.Ordinal)], tenant, cancellationToken);
        var number = await numbers.NextAsync(tenant, cancellationToken);
        var request = PurchaseRequest.CreateDraft(
            tenant, number, caller.UserId, command.Title, command.CostCenterCode, command.SupplierId);
        foreach (var line in command.Lines)
        {
            LinePricing.AddFromCatalog(request, line, prices[line.Sku]);
        }

        db.PurchaseRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return RequestMapper.ToDto(request);
    }
}
