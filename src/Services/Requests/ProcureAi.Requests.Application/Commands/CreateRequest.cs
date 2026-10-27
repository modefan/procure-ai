using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Commands;

/// <summary>One line of a create or update command. The unit price is decimal Rand excl. VAT.</summary>
public sealed record LineInput(string Sku, string Description, int Quantity, decimal UnitPriceExclVat);

public sealed record CreateRequest(string Title, string CostCenterCode, string SupplierId, IReadOnlyList<LineInput> Lines);

public static class CreateRequestHandler
{
    public static async Task<RequestDto> Handle(
        CreateRequest command,
        ICallerContext caller,
        ICallerScope scope,
        IRequestsDbContext db,
        IRequestNumberGenerator numbers,
        CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new DomainException("A tenant is required.");
        await CostCenterOwnership.EnsureAsync(scope, command.CostCenterCode, cancellationToken);
        var number = await numbers.NextAsync(tenant, cancellationToken);
        var request = PurchaseRequest.CreateDraft(
            tenant, number, caller.UserId, command.Title, command.CostCenterCode, command.SupplierId);
        foreach (var line in command.Lines)
        {
            request.AddLine(line.Sku, line.Description, line.Quantity, Money.Zar(line.UnitPriceExclVat));
        }

        db.PurchaseRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return RequestMapper.ToDto(request);
    }
}
