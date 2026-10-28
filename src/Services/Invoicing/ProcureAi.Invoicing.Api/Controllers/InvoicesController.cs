using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Paging;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using ProcureAi.Invoicing.Application;
using ProcureAi.Invoicing.Application.Dtos;
using ProcureAi.Invoicing.Application.Queries;
using ProcureAi.Invoicing.Domain;
using Wolverine;

namespace ProcureAi.Invoicing.Api.Controllers;

/// <summary>
/// Supplier invoices of the caller's tenant, matched against their order and the goods received, and paid by finance. A caller
/// sees the invoices of orders of their own requests and of the cost centers Access assigns them (finance: all); any other invoice
/// is a 404, like one that does not exist. Writes are checked against the version in <c>If-Match</c>; a write that lost a race with
/// another one on the same order is retried, and answered 412 when the invoice itself changed meanwhile.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/invoicing")]
[RequireTenant]
public sealed class InvoicesController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, ILogger<InvoicesController> logger)
    : ControllerBase
{
    private const string What = "The invoice";

    /// <summary>The invoices the caller may see, newest first, optionally of one status or one order.</summary>
    [HttpGet("invoices", Name = "ListInvoices")]
    [CachedGet(Tags = [InvoicingCacheTag.ListTemplate])]
    [RequirePermission(Permissions.InvoicesRead)]
    [ProducesResponseType<PagedResult<InvoiceDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<InvoiceDto>>> List(
        [FromQuery] PagingQuery paging,
        [FromQuery] InvoiceStatus? status = null,
        [FromQuery] Guid? orderId = null,
        CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<InvoiceDto>>(new ListInvoices(status?.ToString(), orderId, paging.Page, paging.Size()), cancellationToken);
    }

    /// <summary>One invoice with its match lines (ordered, received, invoiced before, invoiced, prices, reasons) and its payments.</summary>
    [HttpGet("invoices/{id:guid}", Name = "GetInvoice")]
    [CachedGet(Tags = [InvoicingCacheTag.ItemTemplate])]
    [RequirePermission(Permissions.InvoicesRead)]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await bus.InvokeAsync<InvoiceDto>(new GetInvoice(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(invoice.Version);
        return invoice;
    }

    /// <summary>
    /// Records an invoice typed in by finance against an order the caller may see (404 otherwise) and matches it at once. The same
    /// supplier invoice number again with the same content returns the stored invoice; with other content it is a 409.
    /// </summary>
    [HttpPost("invoices", Name = "CreateInvoice")]
    [Idempotent]
    [RequirePermission(Permissions.InvoicesCreate)]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvoiceDto>> Create(CreateInvoiceBody body, CancellationToken cancellationToken)
    {
        var invoice = (await bus.InvokeOrConflictAsync<InvoiceDto?>(body.ToCommand(), What, cancellationToken))!;
        await InvalidateAsync(invoice.Id);
        Response.Headers.ETag = VersionETag.Format(invoice.Version);
        return invoice;
    }

    /// <summary>
    /// Releases a payment: no body or no lines pays every payable quantity, lines pay chosen quantities, always at the order price.
    /// 403 for the requester of the request (separation of duties), 422 when nothing (or less than asked) is payable or the invoice is
    /// on hold, 412 when the invoice changed since the version in <c>If-Match</c>.
    /// </summary>
    [HttpPost("invoices/{id:guid}/payments", Name = "ReleasePayment")]
    [Idempotent]
    [RequirePermission(Permissions.InvoicesReleasePayment)]
    [RequireIfMatch]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<InvoiceDto>> Pay(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReleasePaymentBody? body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        return await WriteAsync(id, (body ?? new ReleasePaymentBody(null)).ToCommand(id, version), cancellationToken);
    }

    /// <summary>Holds the invoice: nothing is paid on it until the hold is released. Same <c>If-Match</c> rules as paying.</summary>
    [HttpPost("invoices/{id:guid}/hold", Name = "HoldInvoice")]
    [Idempotent]
    [RequirePermission(Permissions.InvoicesReleasePayment)]
    [RequireIfMatch]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<InvoiceDto>> Hold(Guid id, HoldInvoiceBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        return await WriteAsync(id, body.ToCommand(id, version), cancellationToken);
    }

    /// <summary>Releases the hold and matches the invoice again (goods may have arrived meanwhile). Same <c>If-Match</c> rules.</summary>
    [HttpPost("invoices/{id:guid}/release-hold", Name = "ReleaseInvoiceHold")]
    [Idempotent]
    [RequirePermission(Permissions.InvoicesReleasePayment)]
    [RequireIfMatch]
    [ProducesResponseType<InvoiceDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<InvoiceDto>> ReleaseHold(Guid id, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        return await WriteAsync(id, new Application.Commands.ReleaseHold(id, version), cancellationToken);
    }

    private async Task<ActionResult<InvoiceDto>> WriteAsync(Guid id, object command, CancellationToken cancellationToken)
    {
        var invoice = await bus.InvokeOrConflictAsync<InvoiceDto>(command, What, cancellationToken);
        await InvalidateAsync(id);
        Response.Headers.ETag = VersionETag.Format(invoice.Version);
        return invoice;
    }

    /// <summary>
    /// Drops the cached reads of this invoice and of the tenant's lists, so no read after a write sees the old state. Runs after the
    /// commit, so it ignores the client's cancellation, and a failure here is logged, never raised: the cached entries expire by
    /// themselves within the cache duration.
    /// </summary>
    private async Task InvalidateAsync(Guid id)
    {
        try
        {
            await cache.RemoveByTagAsync(InvoicingCacheTag.Of(user.TenantId, id), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for invoice {InvoiceId}; cached reads expire on their own", id);
        }
    }
}
