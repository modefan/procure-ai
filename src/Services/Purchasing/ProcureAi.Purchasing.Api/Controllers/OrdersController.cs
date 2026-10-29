using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using ProcureAi.BuildingBlocks.Web.Paging;
using ProcureAi.Purchasing.Application;
using ProcureAi.Purchasing.Application.Dtos;
using ProcureAi.Purchasing.Application.Queries;
using ProcureAi.Purchasing.Domain;
using Wolverine;

namespace ProcureAi.Purchasing.Api.Controllers;

/// <summary>
/// Purchase orders of the caller's tenant: created by the approval flow (over RabbitMQ), received and closed short here. A caller
/// sees the orders of their own requests and of the cost centers Access assigns them (finance: all); any other order is a 404,
/// like one that does not exist. Writes are checked against the version in <c>If-Match</c>.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/purchasing")]
[RequireTenant]
public sealed class OrdersController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, ILogger<OrdersController> logger)
    : ControllerBase
{
    /// <summary>The orders the caller may see, newest first, optionally of one status.</summary>
    [HttpGet("orders", Name = "ListOrders")]
    [CachedGet(Tags = [PurchasingCacheTag.ListTemplate])]
    [RequirePermission(Permissions.OrdersRead)]
    [ProducesResponseType<PagedResult<PurchaseOrderDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<PurchaseOrderDto>>> List(
        [FromQuery] PagingQuery paging,
        [FromQuery] OrderStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        return await bus.InvokeAsync<PagedResult<PurchaseOrderDto>>(new ListOrders(status?.ToString(), paging.Page, paging.Size()), cancellationToken);
    }

    [HttpGet("orders/{id:guid}", Name = "GetOrder")]
    [CachedGet(Tags = [PurchasingCacheTag.ItemTemplate])]
    [RequirePermission(Permissions.OrdersRead)]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PurchaseOrderDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var order = await bus.InvokeAsync<PurchaseOrderDto>(new GetOrder(id), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(order.Version);
        return order;
    }

    /// <summary>The order of a request (404 while there is none yet, or when the caller may not see it).</summary>
    [HttpGet("requests/{requestId:guid}/order", Name = "GetOrderForRequest")]
    [CachedGet(Tags = [PurchasingCacheTag.ListTemplate])]
    [RequirePermission(Permissions.OrdersRead)]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PurchaseOrderDto>> GetForRequest(Guid requestId, CancellationToken cancellationToken)
    {
        var order = await bus.InvokeAsync<PurchaseOrderDto>(new GetOrderForRequest(requestId), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(order.Version);
        return order;
    }

    /// <summary>
    /// Books a delivery (all or part of what is outstanding). Send the order's version (its ETag) in <c>If-Match</c>: 412 when it
    /// changed since, 422 when a quantity is more than is outstanding or the order cannot receive goods (not confirmed, cancelled).
    /// </summary>
    [HttpPost("orders/{id:guid}/receipts", Name = "ReceiveGoods")]
    [Idempotent]
    [RequirePermission(Permissions.OrdersReceive)]
    [RequireIfMatch]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<PurchaseOrderDto>> Receive(Guid id, ReceiveGoodsBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var order = await bus.InvokeAsync<PurchaseOrderDto>(body.ToCommand(id, version), cancellationToken);
        await InvalidateAsync(id);
        Response.Headers.ETag = VersionETag.Format(order.Version);
        return order;
    }

    /// <summary>
    /// Accepts that the rest of a partly received order will not come (the outstanding quantities are published, so nothing waits
    /// for them). Same <c>If-Match</c> rules as receiving; 422 when nothing was received yet (cancel the request instead).
    /// </summary>
    [HttpPost("orders/{id:guid}/close-short", Name = "CloseOrderShort")]
    [Idempotent]
    [RequirePermission(Permissions.OrdersReceive)]
    [RequireIfMatch]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<PurchaseOrderDto>> CloseShort(Guid id, CloseShortBody body, CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var order = await bus.InvokeAsync<PurchaseOrderDto>(body.ToCommand(id, version), cancellationToken);
        await InvalidateAsync(id);
        Response.Headers.ETag = VersionETag.Format(order.Version);
        return order;
    }

    /// <summary>
    /// Drops the cached reads of this order and of the tenant's lists, so no read after a write sees the old state. Runs after the
    /// commit, so it ignores the client's cancellation, and a failure here is logged, never raised: the cached entries expire by
    /// themselves within the cache duration.
    /// </summary>
    private async Task InvalidateAsync(Guid id)
    {
        try
        {
            await cache.RemoveByTagAsync(PurchasingCacheTag.Of(user.TenantId, id), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for order {OrderId}; cached reads expire on their own", id);
        }
    }
}
