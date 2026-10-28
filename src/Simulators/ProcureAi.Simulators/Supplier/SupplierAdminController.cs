using Microsoft.AspNetCore.Mvc;

namespace ProcureAi.Simulators.Supplier;

/// <summary>Test hooks for the supplier (admin key): make it do something now instead of waiting for its timers.</summary>
[ApiController]
[RequireAdminKey]
[Route("api/v1/simulators/supplier/orders")]
public sealed class SupplierAdminController(SupplierStore store, SupplierService supplier) : ControllerBase
{
    /// <summary>
    /// The orders the supplier holds for one buyer reference (<c>tenant/order number</c>), with how often each was sent: one entry
    /// received once means the buyer sent the order once and it became one supplier order.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SupplierOrderSummary>>(StatusCodes.Status200OK)]
    public IReadOnlyList<SupplierOrderSummary> List([FromQuery, System.ComponentModel.DataAnnotations.Required] string buyerReference) =>
        [.. store.ForBuyerReference(buyerReference).Select(o => new SupplierOrderSummary(o.Id, o.ExternalReference, o.BuyerReference, o.TimesReceived, o.Cancelled))];

    /// <summary>Sends an <c>invoice.issued</c> webhook for the order now (a mismatch demo: another quantity or a price deviation).</summary>
    [HttpPost("{supplierOrderId}/invoice")]
    [ProducesResponseType<InvoiceReceipt>(StatusCodes.Status202Accepted)]
    public IActionResult IssueInvoice(string supplierOrderId, IssueInvoiceRequest request)
    {
        var order = store.Find(supplierOrderId);
        if (order is null)
        {
            return NotFound();
        }

        if (order.Cancelled)
        {
            return Problem("The order is cancelled.", statusCode: StatusCodes.Status409Conflict);
        }

        var (number, error) = supplier.IssueInvoiceNow(order, request);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(request.Lines), error);
            return ValidationProblem(ModelState);
        }

        return Accepted(new InvoiceReceipt(number!));
    }
}
