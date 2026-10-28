using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Purchasing.Application.Abstractions;
using ProcureAi.Purchasing.Application.Commands;
using ProcureAi.Purchasing.Application.Dtos;
using ProcureAi.Purchasing.Application.Mapping;
using ProcureAi.Purchasing.Domain;

namespace ProcureAi.Purchasing.Application.Queries;

/// <summary>The orders the caller may see, newest first. <c>Status</c> is an <see cref="OrderStatus"/> name, any case.</summary>
public sealed record ListOrders(string? Status, int Page, int PageSize);

public sealed record GetOrder(Guid Id);

public sealed record GetOrderForRequest(Guid RequestId);

public static class ListOrdersHandler
{
    public static async Task<PagedResult<PurchaseOrderDto>> Handle(
        ListOrders query, IPurchasingDbContext db, ICallerScope scope, CancellationToken cancellationToken)
    {
        var orders = db.Orders.AsNoTracking().Where(o => o.Number != null);
        if (query.Status is { } text)
        {
            if (!Enum.TryParse<OrderStatus>(text, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            {
                throw new DomainException($"'{text}' is not an order status.");
            }

            orders = orders.Where(o => o.Status == status);
        }

        var callerScope = await scope.GetAsync(cancellationToken);
        if (!callerScope.All)
        {
            var userId = callerScope.UserId;
            var codes = callerScope.CostCenterCodes.ToArray();
            orders = orders.Where(o => o.RequesterId == userId || codes.Contains(o.CostCenterCode));
        }

        var total = await orders.CountAsync(cancellationToken);
        var page = await orders.Include(o => o.Lines).Include(o => o.Receipts)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<PurchaseOrderDto>([.. page.Select(PurchasingMapper.ToDto)], query.Page, query.PageSize, total);
    }
}

public static class GetOrderHandler
{
    public static async Task<PurchaseOrderDto> Handle(
        GetOrder query, IPurchasingDbContext db, ICallerScope scope, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.Receipts)
            .FirstOrDefaultAsync(o => o.Id == query.Id, cancellationToken);
        return PurchasingMapper.ToDto(await OrderAccess.EnsureAllowedAsync(order, scope, query.Id, cancellationToken));
    }
}

public static class GetOrderForRequestHandler
{
    public static async Task<PurchaseOrderDto> Handle(
        GetOrderForRequest query, IPurchasingDbContext db, ICallerScope scope, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.Receipts)
            .FirstOrDefaultAsync(o => o.RequestId == query.RequestId, cancellationToken);
        return PurchasingMapper.ToDto(await OrderAccess.EnsureAllowedAsync(order, scope, $"for request {query.RequestId}", cancellationToken));
    }
}
