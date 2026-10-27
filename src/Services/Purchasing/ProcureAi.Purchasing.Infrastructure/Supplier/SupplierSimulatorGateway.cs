using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ProcureAi.BuildingBlocks.Web.Errors;
using ProcureAi.Purchasing.Application.Abstractions;

namespace ProcureAi.Purchasing.Infrastructure.Supplier;

/// <summary>
/// The anti-corruption layer to the supplier's order API (<c>supplier/v1/orders</c>, the simulator's vocabulary): our order goes
/// out as the supplier's body (prices in cents, <c>articleNumber</c>, our order ID as <c>externalReference</c>, by which the
/// supplier deduplicates, and <c>tenant/PO-1001</c> as <c>buyerReference</c>), and the supplier's answers come back as our
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

    /// <summary>The name of the resilience pipeline (metrics tag <c>pipeline.name</c>).</summary>
    public const string PipelineName = "supplier";

    public const string ApiKeyHeader = "X-Api-Key";
    public const string OrdersPath = "supplier/v1/orders";
    private const string Currency = "ZAR";
    private const int MaxMessageLength = 300;

    public async Task<SupplierAcceptance> SubmitOrderAsync(SupplierOrder order, CancellationToken cancellationToken)
    {
        var body = new SupplierWire.CreateOrderRequest(
            order.OrderId.ToString(),
            $"{order.TenantId}/{order.OrderNumber}",
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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        // The base address and X-Api-Key are on the client (AddSupplierGateway).
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (BrokenCircuitException exception)
        {
            throw new SupplierUnavailableException($"Supplier unavailable (circuit open) for {what}.", "circuit_open", exception);
        }
        catch (TimeoutRejectedException exception)
        {
            throw new SupplierUnavailableException($"The supplier did not answer in time for {what}.", "timeout", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SupplierUnavailableException($"The supplier did not answer in time for {what}.", "timeout", exception);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            throw new SupplierUnavailableException($"The supplier could not be reached for {what}.", "unreachable", exception);
        }
    }

    private async Task EnsureAcceptedAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            throw new SupplierRejectedException(await RefusalMessageAsync(response, cancellationToken));
        }

        if (!DependencyFailures.IsTransientStatus(response.StatusCode))
        {
            logger.LogError("The supplier answered {Status} to {What}; check the supplier client configuration (URL, API key). Retrying", status, what);
        }

        throw new SupplierUnavailableException(
            string.Create(CultureInfo.InvariantCulture, $"Supplier unavailable (HTTP {status}) for {what}."),
            string.Create(CultureInfo.InvariantCulture, $"status_{status}"));
    }

    /// <summary>The supplier's own words: the problem's validation errors or detail, else the status. Kept short (it is stored on the order).</summary>
    private static async Task<string> RefusalMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = string.Create(CultureInfo.InvariantCulture, $"The supplier refused it (HTTP {(int)response.StatusCode}).");
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            string? message = null;
            if (problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                message = string.Join(" ", errors.EnumerateObject()
                    .SelectMany(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.EnumerateArray().Select(v => v.ToString()) : [e.Value.ToString()]));
            }

            if (string.IsNullOrWhiteSpace(message) && problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("detail", out var detail))
            {
                message = detail.ToString();
            }

            message = string.IsNullOrWhiteSpace(message) ? fallback : $"The supplier refused it: {message.Trim()}";
            return message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static long ToCents(decimal rand) => (long)Math.Round(rand * 100m, MidpointRounding.AwayFromZero);
}

/// <summary>The supplier's own types (copies of its published API, never shared code), serialized as camelCase JSON.</summary>
public static class SupplierWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record OrderLine(int LineNumber, string ArticleNumber, int Quantity, long NetPriceCents);

    public sealed record CreateOrderRequest(string ExternalReference, string BuyerReference, string Currency, IReadOnlyList<OrderLine> Lines);

    public sealed record OrderReceipt(string? SupplierOrderId, string? Status);
}
