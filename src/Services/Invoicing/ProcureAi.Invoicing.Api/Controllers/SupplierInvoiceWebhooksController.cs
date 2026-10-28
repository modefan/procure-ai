using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Invoicing.Application.Commands;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Domain;
using Wolverine;

namespace ProcureAi.Invoicing.Api.Controllers;

/// <summary>
/// The supplier's invoice webhook (its own vocabulary: <c>invoice.issued</c>, cents, <c>articleNumber</c>), translated here into
/// <see cref="RecordInvoice"/> with source Webhook. The endpoint is anonymous: the HMAC signature of the raw body is the
/// authentication (<see cref="SignedWebhookAttribute"/>; a bad, old or missing signature never reaches this code and is a 401). The
/// order is the payload's <c>externalReference</c> (our order ID), the supplier its <c>supplierCode</c>; the tenant is always the
/// order's as Invoicing knows it, never the payload's.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/invoicing/webhooks")]
public sealed class SupplierInvoiceWebhooksController(IMessageBus bus, ILogger<SupplierInvoiceWebhooksController> logger) : ControllerBase
{
    public const string InvoiceIssuedType = "invoice.issued";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 200 when the invoice was recorded (matched, or waiting for an order Invoicing does not know yet), and also for a duplicate
    /// (replayed or delivered twice, or the same invoice number with the same content), for a reference that is not one of our orders
    /// and for an event type we do not handle: the supplier must not send any of those again. 409 when the invoice number is already
    /// recorded with other content. 400 when the body cannot be read (not JSON, a required field missing, a value too long to store).
    /// </summary>
    [HttpPost("supplier", Name = "SupplierInvoiceWebhook")]
    [SignedWebhook(RecordInvoiceHandler.WebhookSource)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Supplier(CancellationToken cancellationToken)
    {
        // The raw body is kept as the invoice's document; the signature filter buffered it, so it can be read here.
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync(cancellationToken);
        }

        SupplierInvoiceWebhook? body;
        try
        {
            body = JsonSerializer.Deserialize<SupplierInvoiceWebhook>(raw, Json);
        }
        catch (JsonException)
        {
            return Unreadable("The body is not a supplier invoice event.");
        }

        if (body is null)
        {
            return Unreadable("The body is empty.");
        }

        if (string.IsNullOrWhiteSpace(body.EventId) || body.EventId.Length > WebhookLimits.MaxEventIdLength)
        {
            return Unreadable($"eventId is required and at most {WebhookLimits.MaxEventIdLength} characters.");
        }

        if (body.Type != InvoiceIssuedType)
        {
            logger.LogWarning("Supplier event {EventId} of type {Type} is not handled; ignored", body.EventId, body.Type);
            return Ok();
        }

        if (Invalid(body) is { } problem)
        {
            return Unreadable(problem);
        }

        if (!Guid.TryParse(body.ExternalReference, out var orderId))
        {
            // Not one of our order IDs, so no order of ours: nothing to record against.
            logger.LogWarning("Supplier invoice event {EventId} names no order of ours; ignored", body.EventId);
            return Ok();
        }

        var command = new RecordInvoice(
            orderId, body.InvoiceNumber!.Trim(), body.IssueDate!.Value,
            [.. body.Lines!.Select(l => new InvoiceLineInput(l.LineNumber, l.Quantity, l.NetPriceCents / 100m, string.IsNullOrWhiteSpace(l.ArticleNumber) ? null : l.ArticleNumber.Trim()))],
            body.GrossTotalCents!.Value / 100m, InvoiceSource.Webhook, raw, body.SupplierCode!.Trim(), body.EventId);
        try
        {
            if (!await WebhookInbox.ApplyOnceAsync(() => bus.InvokeAsync<InvoiceDto?>(command, cancellationToken)))
            {
                // The same event committed by a concurrent delivery, still colliding after every retry: recorded once, by the other one.
                logger.LogInformation("Supplier invoice event {EventId} was recorded by a concurrent delivery", body.EventId);
            }
        }
        catch (Exception exception) when (ConcurrencyConflicts.IsLostRace(exception))
        {
            // Another delivery of the same invoice kept winning: the supplier may send it again (it is then a duplicate).
            return Problem(detail: "The invoice was being recorded at the same time; send it again.", statusCode: StatusCodes.Status409Conflict);
        }

        return Ok();
    }

    /// <summary>The first problem of a body that cannot become an invoice, checked before anything touches the database.</summary>
    private static string? Invalid(SupplierInvoiceWebhook body)
    {
        if (string.IsNullOrWhiteSpace(body.InvoiceNumber) || body.InvoiceNumber.Trim().Length > InvoiceLimits.InvoiceNumberLength)
        {
            return $"invoiceNumber is required and at most {InvoiceLimits.InvoiceNumberLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(body.SupplierCode) || body.SupplierCode.Trim().Length > InvoiceLimits.SupplierLength)
        {
            return $"supplierCode is required and at most {InvoiceLimits.SupplierLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(body.ExternalReference))
        {
            return "externalReference is required.";
        }

        if (body.IssueDate is null)
        {
            return "issueDate is required.";
        }

        if (body.GrossTotalCents is not (>= 0 and <= InvoiceLimits.MaxCents))
        {
            return "grossTotalCents is required and must be a positive amount.";
        }

        if (body.Lines is not { Count: > 0 and <= InvoiceLimits.MaxLines })
        {
            return $"lines must hold 1 to {InvoiceLimits.MaxLines} lines.";
        }

        foreach (var line in body.Lines)
        {
            if (line is null)
            {
                return "lines must not contain null.";
            }

            if (line.LineNumber is < 1 or > InvoiceLimits.MaxLines || line.Quantity is < 1 or > InvoiceLimits.MaxQuantity
                || line.NetPriceCents is < 0 or > InvoiceLimits.MaxCents || (line.ArticleNumber?.Trim().Length ?? 0) > InvoiceLimits.SkuLength)
            {
                return $"Line {line.LineNumber} is not a valid invoice line.";
            }
        }

        return null;
    }

    private ObjectResult Unreadable(string detail) =>
        Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Unreadable webhook");
}

/// <summary>The supplier's invoice event as it sends it (camelCase JSON). Our own copy: nothing is shared with the supplier's code.</summary>
public sealed record SupplierInvoiceWebhook(
    string? EventId,
    string? Type,
    string? InvoiceNumber,
    string? SupplierCode,
    string? ExternalReference,
    DateOnly? IssueDate,
    IReadOnlyList<SupplierInvoiceLine>? Lines,
    long? NetTotalCents,
    long? VatCents,
    long? GrossTotalCents);

/// <summary>One line of the supplier's invoice event: our order line number, its article number, quantity and net price in cents.</summary>
public sealed record SupplierInvoiceLine(int LineNumber, string? ArticleNumber, int Quantity, long NetPriceCents);
