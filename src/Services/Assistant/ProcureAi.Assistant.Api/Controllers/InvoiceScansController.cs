using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Scanning;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Options;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>
/// Invoice scanning (plan 05 Task 6): a supplier's PDF invoice is read by the model and checked, for the user to review before entering
/// the invoice in Invoicing (<c>invoices.create</c>). The file is a PDF up to <c>Assistant:Uploads:MaxBytes</c> (10 MB, else 413), checked
/// from its content (else 415). 422 when the PDF holds instructions instead of an invoice, has no text, or the model's answer is no
/// invoice; 503 (<c>Retry-After</c>) when the model is not available. A scan is kept 30 days in the caller's tenant; Invoicing reads it
/// through <c>internal/invoice-scans/{id}</c> to keep it with the invoice.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assistant")]
[RequireTenant]
public sealed class InvoiceScansController(InvoiceScanner scanner, IOptions<AssistantOptions> options, IOptions<ErrorResponseOptions> errors)
    : ControllerBase
{
    /// <summary>
    /// Scans a PDF invoice: what the model read (its numbers as read), a warning for every number that does not add up (with the
    /// recalculated value), the confidence and the model.
    /// </summary>
    [HttpPost("invoice-scans", Name = "ScanInvoice")]
    [Idempotent]
    [RequirePermission(Permissions.InvoicesCreate)]
    [UploadSizeLimit]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<InvoiceScanDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<InvoiceScanDto>> Scan([FromForm] ScanInvoiceForm form, CancellationToken cancellationToken)
    {
        var file = form.File;
        var maxBytes = options.Value.Uploads.MaxBytes;
        if (file.Length > maxBytes)
        {
            return ProblemResult(StatusCodes.Status413PayloadTooLarge, $"The file is larger than {maxBytes / (1024 * 1024)} MB.");
        }

        var bytes = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var fileName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (PolicyFileType.Detect(fileName, bytes) != PolicyFileType.Pdf)
        {
            return ProblemResult(StatusCodes.Status415UnsupportedMediaType, "Upload the invoice as a PDF (.pdf) file.");
        }

        try
        {
            return await scanner.ScanAsync(new ScanInvoice(fileName, bytes), cancellationToken);
        }
        catch (AiProviderUnavailableException)
        {
            Response.Headers.RetryAfter = errors.Value.DependencyRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return ProblemResult(StatusCodes.Status503ServiceUnavailable, "The AI model is not available right now. Try again in a moment.");
        }
    }

    /// <summary>
    /// Internal: a stored scan of the tenant in <c>X-Tenant-Id</c>, for another service (service account or admin API key). A scan of
    /// another tenant is a 404, like one that never existed or has expired.
    /// </summary>
    [HttpGet("internal/invoice-scans/{id:guid}", Name = "GetInvoiceScanInternal")]
    [Authorize(Policy = InternalPolicy.Name)]
    [ProducesResponseType<InvoiceScanRecordDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceScanRecordDto>> GetInternal(Guid id, CancellationToken cancellationToken) =>
        await scanner.GetAsync(id, cancellationToken);

    private static ObjectResult ProblemResult(int status, string title) =>
        new(new ProblemDetails { Status = status, Title = title })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
}

/// <summary>The multipart form of a scan: the PDF invoice (<c>file</c>).</summary>
public sealed class ScanInvoiceForm
{
    [Required]
    [FromForm(Name = "file")]
    public IFormFile File { get; set; } = null!;
}
