using Microsoft.AspNetCore.Mvc;
using ProcureAi.Simulators.Chaos;

namespace ProcureAi.Simulators.Supplier;

/// <summary>The supplier's order API. Slow and sometimes failing, as set by the chaos switch.</summary>
[ApiController]
[RequireSimulatorKey]
[Chaos(ChaosTarget.Supplier)]
[Route("supplier/v1/orders")]
public sealed class SupplierController(SupplierService supplier) : ControllerBase
{
    private const string SupportedCurrency = "ZAR";

    [HttpPost]
    [ProducesResponseType<OrderReceipt>(StatusCodes.Status202Accepted)]
    public IActionResult Create(CreateOrderRequest request)
    {
        if (!string.Equals(request.Currency, SupportedCurrency, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(request.Currency), $"Only {SupportedCurrency} is supported.");
            return ValidationProblem(ModelState);
        }

        return Accepted(supplier.Receive(request));
    }

    [HttpPost("{supplierOrderId}/cancel")]
    [ProducesResponseType<CancelReceipt>(StatusCodes.Status200OK)]
    public IActionResult Cancel(string supplierOrderId) =>
        supplier.Cancel(supplierOrderId) is { } receipt ? Ok(receipt) : NotFound();
}
