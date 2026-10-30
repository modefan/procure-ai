using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Invoicing.Application.Abstractions;

namespace ProcureAi.Invoicing.Infrastructure.Erp;

/// <summary>
/// The <c>ErpClient</c> section: the ERP's address, the key we present and the resilience pipeline of every call (the shape and
/// values of <c>SupplierClient</c>).
/// </summary>
public sealed class ErpClientOptions : ExternalSystemClientOptions
{
    public const string SectionName = "ErpClient";
}

/// <summary>
/// The anti-corruption layer to the ERP's payment API (<c>erp/v1/payments</c>, the simulator's vocabulary): our payment goes out as
/// the ERP's body (our payment ID as <c>paymentReference</c>, by which the ERP deduplicates; the order's supplier as
/// <c>vendorCode</c>; amounts in cents) and the ERP's document number comes back. 200 (already posted) and 201 are acceptances; 400 and
/// 422 are a refusal for good (<see cref="ErpRejectedException"/>); a timeout, 408, 429, 5xx, an open circuit or a failed connection is
/// "try again later" (<see cref="ErpUnavailableException"/>). Any other answer (401 for a wrong key) is a fault on our side: logged as
/// an error and treated as unavailable, so the payment keeps retrying visibly. The ERP's types are copies kept here.
/// </summary>
public sealed class ErpSimulatorGateway(HttpClient http, ILogger<ErpSimulatorGateway> logger) : IErpGateway
{
    /// <summary>The name of the typed client and of its resilience pipeline (metrics tag <c>pipeline.name</c>).</summary>
    public const string ClientName = "erp";

    /// <summary>The name of its circuit breaker on the system status screen.</summary>
    public const string BreakerName = "ERP";

    public const string PaymentsPath = "erp/v1/payments";
    private const string Currency = "ZAR";

    public async Task<string> ExportPaymentAsync(ErpPayment payment, CancellationToken cancellationToken)
    {
        var what = $"payment {payment.PaymentNumber}";
        var body = new ErpWire.CreatePaymentRequest(
            payment.PaymentId.ToString(),
            payment.SupplierId,
            payment.SupplierInvoiceNumber,
            Currency,
            ExternalSystemClient.ToCents(payment.TotalInclVat.Amount),
            [.. payment.Lines.OrderBy(l => l.LineNo).Select(l => new ErpWire.PaymentLine(l.LineNo, l.Sku, l.Quantity, ExternalSystemClient.ToCents(l.AmountExclVat.Amount)))]);
        using var request = new HttpRequestMessage(HttpMethod.Post, PaymentsPath) { Content = JsonContent.Create(body, options: ErpWire.Json) };
        // The base address and X-Api-Key are on the client (AddErpGateway).
        using var response = await ExternalSystemClient.SendAsync(
            http, request, "ERP", what, (message, _, inner) => new ErpUnavailableException(message, inner), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                throw new ErpRejectedException(await ExternalSystemClient.RefusalMessageAsync(response, "The ERP refused the payment", cancellationToken));
            }

            if (!DependencyFailures.IsTransientStatus(response.StatusCode))
            {
                logger.LogError("The ERP answered {Status} to {What}; check the ERP client configuration (URL, API key). Retrying", status, what);
            }

            throw new ErpUnavailableException(string.Create(CultureInfo.InvariantCulture, $"ERP unavailable (HTTP {status}) for {what}."));
        }

        ErpWire.PaymentReceipt? receipt;
        try
        {
            receipt = await response.Content.ReadFromJsonAsync<ErpWire.PaymentReceipt>(ErpWire.Json, cancellationToken);
        }
        catch (JsonException exception)
        {
            // Posted, but we cannot tell under which document: ask again (the ERP deduplicates by our payment ID).
            throw new ErpUnavailableException("The ERP took the payment with an unreadable answer.", exception);
        }

        return string.IsNullOrWhiteSpace(receipt?.DocumentNumber)
            ? throw new ErpUnavailableException("The ERP took the payment without a document number.")
            : receipt.DocumentNumber;
    }
}

/// <summary>The ERP's own types (copies of its published API, never shared code), serialized as camelCase JSON.</summary>
public static class ErpWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record PaymentLine(int LineNumber, string? ArticleNumber, int? Quantity, long? NetAmountCents);

    public sealed record CreatePaymentRequest(
        string PaymentReference, string VendorCode, string InvoiceNumber, string Currency, long GrossAmountCents, IReadOnlyList<PaymentLine> Lines);

    public sealed record PaymentReceipt(string? DocumentNumber);
}
