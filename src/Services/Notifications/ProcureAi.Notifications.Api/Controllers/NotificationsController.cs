using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.Notifications.Application.Inbox;

namespace ProcureAi.Notifications.Api.Controllers;

/// <summary>
/// The caller's bell: the newest notifications (at most <c>Notifications:Bell:MaxItems</c>, within <c>Retention</c>) and how many are
/// unread. Only ever the caller's own items, of their own tenant (the user is the token's <c>sub</c>, or <c>X-On-Behalf-Of-User</c> for
/// the admin key). New items also arrive live as <c>NotificationAdded</c> on the hub.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
[RequireTenant]
public sealed class NotificationsController(
    BellInbox inbox, ICacheInvalidator cache, ICurrentUser user, ILogger<NotificationsController> logger) : ControllerBase
{
    private const int MaxIdLength = 64;

    /// <summary>The caller's bell, newest first, with the unread count.</summary>
    [HttpGet(Name = "ListNotifications")]
    [CachedGet(Tags = [PersonalNotifier.CacheTagTemplate])]
    [ProducesResponseType<BellList>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<BellList>> List(CancellationToken cancellationToken)
    {
        var (tenantId, userId) = Owner();
        return await inbox.ListAsync(tenantId, userId, cancellationToken);
    }

    /// <summary>Marks one of the caller's notifications read. 404 for an item the caller does not have.</summary>
    [HttpPost("{id}/read", Name = "MarkNotificationRead")]
    [Idempotent]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead([MaxLength(MaxIdLength)] string id, CancellationToken cancellationToken)
    {
        var (tenantId, userId) = Owner();
        await inbox.MarkReadAsync(tenantId, userId, id, cancellationToken);
        await InvalidateAsync(tenantId);
        return NoContent();
    }

    /// <summary>Marks all of the caller's notifications read.</summary>
    [HttpPost("read-all", Name = "MarkAllNotificationsRead")]
    [Idempotent]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var (tenantId, userId) = Owner();
        await inbox.MarkAllReadAsync(tenantId, userId, cancellationToken);
        await InvalidateAsync(tenantId);
        return NoContent();
    }

    /// <summary>Whose bell: the tenant ([RequireTenant] made sure there is one) and the user, never taken from the URL or body.</summary>
    private (string TenantId, string UserId) Owner()
    {
        var userId = user.OnBehalfOfUser ?? user.UserId;
        if (user.TenantId is not { } tenantId || string.IsNullOrEmpty(userId) || userId == CurrentUser.AnonymousUserId)
        {
            throw new ForbiddenException("The caller has no user to show notifications for.");
        }

        return (tenantId, userId);
    }

    /// <summary>
    /// Drops the tenant's cached bell reads after a write, so the next read shows it (the cache key is per user; the tag per tenant). A
    /// failure is logged, never raised: the cached entries expire within a second.
    /// </summary>
    private async Task InvalidateAsync(string tenantId)
    {
        try
        {
            if (CacheTag.Format(PersonalNotifier.CacheTagTemplate, CacheTag.Tenant(tenantId)) is { } tag)
            {
                await cache.RemoveByTagAsync([tag], CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for the notifications of {TenantId}; cached reads expire on their own", tenantId);
        }
    }
}
