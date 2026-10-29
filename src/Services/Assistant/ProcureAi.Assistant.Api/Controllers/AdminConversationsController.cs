using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using ProcureAi.BuildingBlocks.Web.Paging;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>
/// Every conversation of the caller's tenant, with everything stored about each answer: guard flags, timings, usage (plan 05 ruling 18,
/// <c>admin.system</c>). A conversation of another tenant is a 404.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assistant/admin/conversations")]
[RequireTenant]
public sealed class AdminConversationsController(ConversationService conversations) : ControllerBase
{
    /// <summary>The tenant's conversations (of one user with <c>userId</c>), newest change first.</summary>
    [HttpGet(Name = "AdminListConversations")]
    [CachedGet(Tags = [ConversationCacheTag.ListTemplate])]
    [RequirePermission(Permissions.AdminSystem)]
    [ProducesResponseType<PagedResult<AdminConversationListItem>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<AdminConversationListItem>>> List(
        [FromQuery] string? userId, [FromQuery] PagingQuery paging, CancellationToken cancellationToken) =>
        await conversations.AdminListAsync(userId, paging.Page, paging.Size(), cancellationToken);

    /// <summary>The full record of one conversation.</summary>
    [HttpGet("{id:guid}", Name = "AdminGetConversation")]
    [CachedGet(Tags = [ConversationCacheTag.ConversationTemplate])]
    [RequirePermission(Permissions.AdminSystem)]
    [ProducesResponseType<AdminConversationDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminConversationDto>> Get(Guid id, CancellationToken cancellationToken) =>
        await conversations.AdminGetAsync(id, cancellationToken);
}
