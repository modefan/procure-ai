using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.Simulators.Chaos;

namespace ProcureAi.Simulators.Erp;

public sealed record PaymentLineDto(
    int LineNumber,
    string? ArticleNumber,
    int? Quantity,
    long? NetAmountCents);

public sealed record CreatePaymentRequest(
    [Required, StringLength(128, MinimumLength = 1)] string PaymentReference,
    [Required, StringLength(64, MinimumLength = 1)] string VendorCode,
    [Required, StringLength(64, MinimumLength = 1)] string InvoiceNumber,
    [Required] string Currency,
    [Range(1, long.MaxValue)] long GrossAmountCents,
    List<PaymentLineDto>? Lines);

public sealed record PaymentReceipt(string DocumentNumber);

public sealed record PaymentDocument(
    string DocumentNumber,
    string PaymentReference,
    string VendorCode,
    string InvoiceNumber,
    string Currency,
    long GrossAmountCents,
    IReadOnlyList<PaymentLineDto> Lines,
    DateTimeOffset PostedAt);

/// <summary>The ERP's payment API. Idempotent by payment reference; slow and sometimes failing, as set by the chaos switch.</summary>
[ApiController]
[RequireSimulatorKey]
[Chaos(ChaosTarget.Erp)]
[Route("erp/v1/payments")]
public sealed class ErpController(ErpStore store, TimeProvider time) : ControllerBase
{
    private const string SupportedCurrency = "ZAR";

    [HttpPost]
    [ProducesResponseType<PaymentReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<PaymentReceipt>(StatusCodes.Status200OK)]
    public IActionResult Create(CreatePaymentRequest request)
    {
        if (!string.Equals(request.Currency, SupportedCurrency, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(request.Currency), $"Only {SupportedCurrency} is supported.");
            return ValidationProblem(ModelState);
        }

        var (document, created) = store.GetOrAdd(request, time.GetUtcNow());
        var receipt = new PaymentReceipt(document.DocumentNumber);
        return created
            ? Created($"erp/v1/payments/{Uri.EscapeDataString(request.PaymentReference)}", receipt)
            : Ok(receipt);
    }

    [HttpGet("{paymentReference}")]
    [ProducesResponseType<PaymentDocument>(StatusCodes.Status200OK)]
    public IActionResult Get(string paymentReference) =>
        store.Find(paymentReference) is { } document ? Ok(document) : NotFound();
}
