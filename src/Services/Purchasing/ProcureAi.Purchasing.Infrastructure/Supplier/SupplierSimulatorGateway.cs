using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.Purchasing.Application.Abstractions;

namespace ProcureAi.Purchasing.Infrastructure.Supplier;

/// <summary>
/// The anti-corruption layer to the supplier's order API (<c>supplier/v1/orders</c>, the simulator's vocabulary): our order goes
/// out as the supplier's body (prices in cents, <c>articleNumber</c>, our order ID as <c>externalReference</c>, by which the
/// supplier deduplicates, <c>tenant/PO-1001</c> as <c>buyerReference</c>, and the order's supplier as <c>supplierCode</c>, which the
/// supplier quotes on its invoices), and the supplier's answers come back as our
/// outcomes. 202 is an acceptance; 400 and 422 are a refusal for good (<see cref="SupplierRejectedException"/> with the supplier's
/// message); a timeout, 408, 429, 5xx, an open circuit or a failed connection is "try again later"
/// (<see cref="SupplierUnavailableException"/>). Any other answer (401 for a wrong key, 404 on submit) is a fault on our side, not the
/// order's: it is logged as an error and treated as unavailable too, so the order keeps retrying visibly instead of being rejected.
/// The supplier's types are copies kept here (<see cref="SupplierWire"/>): nothing outside Infrastructure sees them.
/// </summary>
public sealed class SupplierSimulatorGateway(HttpClient http, ILogger<SupplierSimulatorGateway> logger) : ISupplierGateway
{
    /// <summary>The name of the typed client (and so of its resilience pipeline and primary handler).</summary>
    public const string ClientName = "supplier";

    public const string OrdersPath = "supplier/v1/orders";
    private const string Currency = "ZAR";

    public async Task<SupplierAcceptance> SubmitOrderAsync(SupplierOrder order, CancellationToken cancellationToken)
    {
        var body = new SupplierWire.CreateOrderRequest(
            order.OrderId.ToString(),
            $"{order.TenantId}/{order.OrderNumber}",
            order.SupplierId,
            Currency,
            [.. order.Lines.OrderBy(l => l.LineNo).Select(l => new SupplierWire.OrderLine(l.LineNo, l.Sku, l.Quantity, ToCents(l.UnitPriceExclVat.Amount)))]);
        using var request = new HttpRequestMessage(HttpMethod.Post, OrdersPath) { Content = JsonContent.Create(body, options: SupplierWire.Json) };
        using var response = await SendAsync(request, $"order {order.OrderNumber}", cancellationToken);
        await EnsureAcceptedAsync(response, $"order {order.OrderNumber}", cancellationToken);
        SupplierWire.OrderReceipt? receipt;
        try
        {
            receipt = await response.Content.ReadFromJsonAsync<SupplierWire.OrderReceipt>(SupplierWire.Json, cancellationToken);
        }
        catch (JsonException exception)
        {
            // Accepted, but we cannot tell under which reference: ask again (the supplier deduplicates by our order ID).
            throw new SupplierUnavailableException("The supplier accepted the order with an unreadable answer.", "bad_answer", exception);
        }

        return string.IsNullOrWhiteSpace(receipt?.SupplierOrderId)
            ? throw new SupplierUnavailableException("The supplier accepted the order without a reference.", "bad_answer")
            : new SupplierAcceptance(receipt.SupplierOrderId);
    }

    public async Task CancelOrderAsync(string supplierReference, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{OrdersPath}/{Uri.EscapeDataString(supplierReference)}/cancel");
        using var response = await SendAsync(request, $"the cancel of {supplierReference}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SupplierRejectedException($"The supplier does not know order {supplierReference}.");
        }

        await EnsureAcceptedAsync(response, $"the cancel of {supplierReference}", cancellationToken);
    }

    // The base address and X-Api-Key are on the client (AddSupplierGateway).
    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken) =>
        ExternalSystemClient.SendAsync(
            http, request, "supplier", what, (message, reason, inner) => new SupplierUnavailableException(message, reason, inner), cancellationToken);

    private async Task EnsureAcceptedAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            throw new SupplierRejectedException(await ExternalSystemClient.RefusalMessageAsync(response, "The supplier refused it", cancellationToken));
        }

        if (!DependencyFailures.IsTransientStatus(response.StatusCode))
        {
            logger.LogError("The supplier answered {Status} to {What}; check the supplier client configuration (URL, API key). Retrying", status, what);
        }

        throw new SupplierUnavailableException(
            string.Create(CultureInfo.InvariantCulture, $"Supplier unavailable (HTTP {status}) for {what}."),
            string.Create(CultureInfo.InvariantCulture, $"status_{status}"));
    }

    private static long ToCents(decimal rand) => ExternalSystemClient.ToCents(rand);
}

/// <summary>The supplier's own types (copies of its published API, never shared code), serialized as camelCase JSON.</summary>
public static class SupplierWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record OrderLine(int LineNumber, string ArticleNumber, int Quantity, long NetPriceCents);

    /// <summary><c>SupplierCode</c> is our catalog code of the supplier; the supplier quotes it on its invoices, so Invoicing can check the invoice is from the order's supplier.</summary>
    public sealed record CreateOrderRequest(string ExternalReference, string BuyerReference, string SupplierCode, string Currency, IReadOnlyList<OrderLine> Lines);

    public sealed record OrderReceipt(string? SupplierOrderId, string? Status);
}
