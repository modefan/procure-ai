using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ProcureAi.Budgets.Application;
using ProcureAi.Budgets.Application.Commands;
using ProcureAi.Budgets.Application.Dtos;
using ProcureAi.Budgets.Application.Queries;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Concurrency;
using Wolverine;

namespace ProcureAi.Budgets.Api.Controllers;

/// <summary>
/// Cost center budgets of the caller tenant, in Rand. Reservations are made over RabbitMQ by the approval flow, not here.
/// A change is checked against the version in <c>If-Match</c>.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/budgets")]
[RequireTenant]
public sealed class BudgetsController(
    IMessageBus bus, ICacheInvalidator cache, ICurrentUser user, BudgetScope scope, ILogger<BudgetsController> logger) : ControllerBase
{
    /// <summary>The budgets the caller may see, optionally of one fiscal year (<c>FY2027</c>). Callers who do not manage budgets see only their cost centers.</summary>
    [HttpGet(Name = "ListBudgets")]
    [CachedGet(Tags = [BudgetCacheTag.Template])]
    [RequirePermission(Permissions.BudgetsRead)]
    [ProducesResponseType<IReadOnlyList<BudgetDto>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<BudgetDto>>> List(
        [FromQuery, MaxLength(BudgetLimits.MaxFiscalYearLength)] string? fiscalYear, CancellationToken cancellationToken)
    {
        var budgets = await bus.InvokeAsync<IReadOnlyList<BudgetDto>>(new ListBudgets(fiscalYear), cancellationToken);
        var visible = await scope.VisibleCostCentersAsync(cancellationToken);
        return Ok(visible is null ? budgets : budgets.Where(b => visible.Contains(b.CostCenterCode)).ToList());
    }

    /// <summary>Whether an amount in Rand fits what is left of the cost center budget of the current fiscal year (the live check of the request form).</summary>
    [HttpGet("{costCenter}/check", Name = "CheckBudget")]
    [CachedGet(Tags = [BudgetCacheTag.Template])]
    [RequirePermission(Permissions.RequestsCreate)]
    [ProducesResponseType<BudgetCheckDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BudgetCheckDto>> Check(
        [MaxLength(BudgetLimits.MaxCodeLength)] string costCenter,
        [FromQuery, Range(0, BudgetLimits.MaxAmount)] decimal amount,
        CancellationToken cancellationToken) =>
        await bus.InvokeAsync<BudgetCheckDto>(new CheckBudget(costCenter, amount), cancellationToken);

    [HttpGet("{costCenter}/{fiscalYear}", Name = "GetBudget")]
    [CachedGet(Tags = [BudgetCacheTag.Template])]
    [RequirePermission(Permissions.BudgetsRead)]
    [ProducesResponseType<BudgetDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BudgetDto>> Get(
        [MaxLength(BudgetLimits.MaxCodeLength)] string costCenter,
        [MaxLength(BudgetLimits.MaxFiscalYearLength)] string fiscalYear,
        CancellationToken cancellationToken)
    {
        // A cost center the caller may not see looks the same as one that does not exist.
        var visible = await scope.VisibleCostCentersAsync(cancellationToken);
        if (visible is not null && !visible.Contains(costCenter))
        {
            throw new NotFoundException($"No budget for {costCenter} in {fiscalYear}.");
        }

        var budget = await bus.InvokeAsync<BudgetDto>(new GetBudget(costCenter, fiscalYear), cancellationToken);
        Response.Headers.ETag = VersionETag.Format(budget.Version);
        return budget;
    }

    /// <summary>Sets the budget. Send the budget version (<c>version</c>, also the ETag of the read) in <c>If-Match</c>. It cannot go below what is reserved or spent.</summary>
    [HttpPut("{costCenter}/{fiscalYear}", Name = "SetBudget")]
    [RequirePermission(Permissions.BudgetsManage)]
    [RequireIfMatch]
    [ProducesResponseType<BudgetDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<BudgetDto>> Set(
        [MaxLength(BudgetLimits.MaxCodeLength)] string costCenter,
        [MaxLength(BudgetLimits.MaxFiscalYearLength)] string fiscalYear,
        SetBudgetBody body,
        CancellationToken cancellationToken)
    {
        if (Request.ExpectedVersion() is not { } version)
        {
            return IfMatchExtensions.PreconditionFailed("If-Match is not a version tag of this resource.");
        }

        var updated = await bus.InvokeAsync<BudgetDto>(new SetBudgetAmount(costCenter, fiscalYear, body.Amount, version), cancellationToken);
        await InvalidateAsync();
        Response.Headers.ETag = VersionETag.Format(updated.Version);
        return updated;
    }

    /// <summary>
    /// Drops the cached budget reads of the tenant after a write, so no read after it sees the old state. Runs after the commit,
    /// so it ignores the cancellation of the client, and a failure here is logged, never raised: the cached entries
    /// expire by themselves within the cache duration.
    /// </summary>
    private async Task InvalidateAsync()
    {
        try
        {
            await cache.RemoveByTagAsync([BudgetCacheTag.Tenant(user.TenantId)], CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for the budgets of {Tenant}; cached reads expire on their own", user.TenantId);
        }
    }
}
