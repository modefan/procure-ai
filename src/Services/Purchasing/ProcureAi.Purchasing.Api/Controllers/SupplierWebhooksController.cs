using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.BuildingBlocks.Web.Webhooks;
using ProcureAi.Purchasing.Application.Commands;
using Wolverine;

namespace ProcureAi.Purchasing.Api.Controllers;

/// <summary>
/// The supplier's order webhooks (its own vocabulary: <c>order.acknowledged</c>, <c>order.declined</c>), translated here into
/// <see cref="ApplySupplierOrderEvent"/>. The endpoint is anonymous: the HMAC signature of the raw body is the authentication
/// (<see cref="SignedWebhookAttribute"/>; a bad, old or missing signature never reaches this code and is a 401). The tenant is always
/// the stored order's, never the payload's.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/purchasing/webhooks")]
public sealed class SupplierWebhooksController(IMessageBus bus, ILogger<SupplierWebhooksController> logger) : ControllerBase
{
    public const string AcknowledgedType = "order.acknowledged";
    public const string DeclinedType = "order.declined";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 200 when the event was applied, and also when it was a duplicate (replayed or delivered twice), for an unknown order and for
    /// an event type we do not handle: the supplier must not send any of those again. 400 when the body cannot be read (not JSON,
    /// a required field missing, a value too long to store), which retrying the same bytes cannot fix either.
    /// </summary>
    [HttpPost("supplier", Name = "SupplierWebhook")]
    [SignedWebhook(ApplySupplierOrderEventHandler.WebhookSource)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Supplier(CancellationToken cancellationToken)
    {
        SupplierOrderWebhook? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<SupplierOrderWebhook>(Request.Body, Json, cancellationToken);
        }
        catch (JsonException)
        {
            return Unreadable("The body is not a supplier order event.");
        }

        if (body is null)
        {
            return Unreadable("The body is empty.");
        }

        if (string.IsNullOrWhiteSpace(body.EventId) || body.EventId.Length > WebhookLimits.MaxEventIdLength)
        {
            return Unreadable($"eventId is required and at most {WebhookLimits.MaxEventIdLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(body.SupplierOrderId) || body.SupplierOrderId.Length > SupplierEventLimits.ReferenceLength)
        {
            return Unreadable($"supplierOrderId is required and at most {SupplierEventLimits.ReferenceLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(body.ExternalReference))
        {
            return Unreadable("externalReference is required.");
        }

        SupplierOrderEventKind kind;
        switch (body.Type)
        {
            case AcknowledgedType:
                kind = SupplierOrderEventKind.Confirmed;
                break;
            case DeclinedType:
                kind = SupplierOrderEventKind.Declined;
                break;
            default:
                logger.LogWarning("Supplier event {EventId} of type {Type} is not handled; ignored", body.EventId, body.Type);
                return Ok();
        }

        if (!Guid.TryParse(body.ExternalReference, out var orderId))
        {
            // Not one of our order IDs, so no order of ours: the same as an unknown order.
            logger.LogWarning("Supplier event {EventId} names no order of ours; ignored", body.EventId);
            return Ok();
        }

        var reason = body.DeclineReason is { Length: > SupplierEventLimits.ReasonLength } text ? text[..SupplierEventLimits.ReasonLength] : body.DeclineReason;
        var command = new ApplySupplierOrderEvent(body.EventId, kind, orderId, body.SupplierOrderId, body.ExpectedDeliveryDate, reason);
        if (!await WebhookInbox.ApplyOnceAsync(() => bus.InvokeAsync(command, cancellationToken)))
        {
            // The same event committed by a concurrent delivery, still colliding after every retry: applied once, by the other one.
            logger.LogInformation("Supplier event {EventId} was applied by a concurrent delivery", body.EventId);
        }

        return Ok();
    }

    private ObjectResult Unreadable(string detail) =>
        Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Unreadable webhook");
}

/// <summary>The supplier's order event as it sends it (camelCase JSON). Our own copy: nothing is shared with the supplier's code.</summary>
public sealed record SupplierOrderWebhook(
    string? EventId,
    string? Type,
    string? SupplierOrderId,
    string? ExternalReference,
    DateOnly? ExpectedDeliveryDate,
    string? DeclineReason);

/// <summary>The sizes of the order columns a supplier event writes to.</summary>
public static class SupplierEventLimits
{
    public const int ReferenceLength = 100;
    public const int ReasonLength = 500;
}
