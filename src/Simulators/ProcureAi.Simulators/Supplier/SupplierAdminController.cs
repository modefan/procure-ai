using Microsoft.AspNetCore.Mvc;

namespace ProcureAi.Simulators.Supplier;

/// <summary>Test hooks for the supplier (admin key): make it do something now instead of waiting for its timers.</summary>
[ApiController]
[RequireAdminKey]
[Route("api/v1/simulators/supplier/orders")]
public sealed class SupplierAdminController(SupplierStore store, SupplierService supplier) : ControllerBase
{
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
