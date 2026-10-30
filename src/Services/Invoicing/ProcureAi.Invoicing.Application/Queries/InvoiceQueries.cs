using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Invoicing.Application.Abstractions;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Application.Mapping;
using ProcureAi.Invoicing.Domain;

namespace ProcureAi.Invoicing.Application.Queries;

/// <summary>The invoices the caller may see, newest first. <c>Status</c> is an <see cref="InvoiceStatus"/> name, any case.</summary>
public sealed record ListInvoices(string? Status, Guid? OrderId, int Page, int PageSize);

public sealed record GetInvoice(Guid Id);

public static class ListInvoicesHandler
{
    /// <summary>
    /// The caller's tenant (query filter) and scope: the invoices of orders of their own requests or of their cost centers, taken from
    /// the order view (finance and the admin key: all).
    /// </summary>
    public static async Task<PagedResult<InvoiceDto>> Handle(
        ListInvoices query, IInvoicingDbContext db, ICallerScope scope, IAccessDirectory access, ICallerContext caller,
        CancellationToken cancellationToken)
    {
        var orders = db.Orders.AsNoTracking();
        var callerScope = await scope.GetAsync(cancellationToken);
        if (!callerScope.All)
        {
            var userId = callerScope.UserId;
            var codes = callerScope.CostCenterCodes.ToArray();
            orders = orders.Where(o => o.RequesterId == userId || codes.Contains(o.CostCenterCode));
        }

        var invoices = db.Invoices.AsNoTracking().Where(i => orders.Any(o => o.Id == i.OrderId));
        if (query.Status is { } text)
        {
            if (!Enum.TryParse<InvoiceStatus>(text, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            {
                throw new DomainException($"'{text}' is not an invoice status.");
            }

            invoices = invoices.Where(i => i.Status == status);
        }

        if (query.OrderId is { } orderId)
        {
            invoices = invoices.Where(i => i.OrderId == orderId);
        }

        var total = await invoices.CountAsync(cancellationToken);
        var page = await invoices.Include(i => i.Lines).Include(i => i.Payments)
            .OrderByDescending(i => i.ReceivedAt).ThenByDescending(i => i.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        var orderIds = page.Select(i => i.OrderId).Distinct().ToArray();
        var views = await db.Orders.AsNoTracking().Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        var names = await PaymentNames.LookupAsync(
            access, page.SelectMany(i => i.Payments).Select(p => p.ReleasedBy), caller.TenantId ?? "", cancellationToken);
        return new PagedResult<InvoiceDto>(
            [.. page.Select(i => InvoicingMapper.ToDto(i, views.GetValueOrDefault(i.OrderId)).WithNames(names))], query.Page, query.PageSize, total);
    }
}

public static class GetInvoiceHandler
{
    /// <summary>Missing, another tenant's and out of the caller's scope are all 404.</summary>
    public static async Task<InvoiceDto> Handle(
        GetInvoice query, IInvoicingDbContext db, ICallerScope scope, IAccessDirectory access, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == query.Id, cancellationToken)
            ?? throw new NotFoundException($"Invoice {query.Id} was not found.");
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == invoice.OrderId, cancellationToken);
        if (order is null || !(await scope.GetAsync(cancellationToken)).Allows(order.RequesterId, order.CostCenterCode))
        {
            throw new NotFoundException($"Invoice {query.Id} was not found.");
        }

        return await InvoicingMapper.ToDto(invoice, order).WithNamesAsync(access, invoice.TenantId, cancellationToken);
    }
}
