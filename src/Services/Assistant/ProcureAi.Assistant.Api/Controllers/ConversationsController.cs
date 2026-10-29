using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>
/// The caller's conversations with the assistant (<c>assistant.use</c>), in the caller's tenant. The owner is the caller (for the admin API
/// key, <c>X-On-Behalf-Of-User</c>); another user's or tenant's conversation is a 404, the same as one that never existed.
/// <para>
/// A question is posted to <c>{id}/messages</c> and answered as a <c>text/event-stream</c> (plan 05 ruling 6): events <c>tool_call</c>,
/// <c>tool_result</c>, <c>sources</c>, <c>delta</c>, <c>replace</c>, <c>done</c>, <c>error</c>, each with camelCase JSON data
/// (<see cref="SseEvent"/>). It has no <c>[Idempotent]</c> and no cache, which would buffer the stream: one answer at a time per
/// conversation instead (409 while one streams). Before the stream starts, failures are plain problems: 400, 404, 409, 422 (nothing to
/// answer again), 429 (the daily quota, <c>Retry-After</c>); once it started, an <c>error</c> event.
/// </para>
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/assistant")]
[RequireTenant]
public sealed class ConversationsController(ConversationService conversations, AnswerStreamer answers) : ControllerBase
{
    /// <summary>The caller's newest conversations (at most 20), by last change.</summary>
    [HttpGet("conversations", Name = "ListConversations")]
    [CachedGet(Tags = [ConversationCacheTag.ListTemplate])]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType<IReadOnlyList<ConversationListItem>>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<IReadOnlyList<ConversationListItem>>> List(CancellationToken cancellationToken) =>
        Ok(await conversations.ListAsync(cancellationToken));

    /// <summary>Starts an empty conversation; its title is the first question.</summary>
    [HttpPost("conversations", Name = "CreateConversation")]
    [Idempotent]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType<CreatedConversation>(StatusCodes.Status201Created, "application/json")]
    public async Task<ActionResult<CreatedConversation>> Create(CancellationToken cancellationToken)
    {
        var created = await conversations.CreateAsync(cancellationToken);
        return Created($"/api/v1/assistant/conversations/{created.Id}", created);
    }

    /// <summary>One conversation with its messages: text, status, sources, tool steps and usage.</summary>
    [HttpGet("conversations/{id:guid}", Name = "GetConversation")]
    [CachedGet(Tags = [ConversationCacheTag.ConversationTemplate])]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType<ConversationDto>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDto>> Get(Guid id, CancellationToken cancellationToken) =>
        await conversations.GetAsync(id, cancellationToken);

    /// <summary>Deletes the conversation and its messages.</summary>
    [HttpDelete("conversations/{id:guid}", Name = "DeleteConversation")]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await conversations.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's questions today (South African calendar day) and the daily limit.</summary>
    [HttpGet("quota", Name = "GetAssistantQuota")]
    [CachedGet(Tags = [ConversationCacheTag.QuotaTemplate])]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType<QuotaDto>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<QuotaDto>> Quota(CancellationToken cancellationToken) =>
        await conversations.QuotaAsync(cancellationToken);

    /// <summary>
    /// Asks a question (<c>mode</c> <c>new</c>), answers the last one again (<c>regenerate</c>, the text is not used) or replaces the last
    /// question and its answer (<c>edit</c>). The answer streams as server-sent events (see the controller's description).
    /// </summary>
    [HttpPost("conversations/{id:guid}/messages", Name = "AskQuestion")]
    [RequirePermission(Permissions.AssistantUse)]
    [ProducesResponseType<string>(StatusCodes.Status200OK, "text/event-stream")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    public async Task<IResult> Ask(Guid id, [FromBody] AskQuestionRequest request, CancellationToken cancellationToken)
    {
        var start = await answers.StartAsync(new AskQuestion(id, request.Text, request.Mode!.Value), cancellationToken);
        switch (start)
        {
            case AnswerStart.Streaming streaming:
                // The stream releases the lock when it ends; this covers a response that never enumerates it (the client left at once).
                HttpContext.Response.RegisterForDisposeAsync(streaming.Lock);
                return TypedResults.ServerSentEvents(Items(streaming.Events, cancellationToken));
            case AnswerStart.Refused refused:
                if (refused.RetryAfter is { } wait)
                {
                    Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return TypedResults.Problem(refused.Detail, statusCode: refused.Status, title: refused.Title);
            default:
                throw new InvalidOperationException("Unknown answer start.");
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> Items(
        IAsyncEnumerable<SseEvent> events, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            yield return new SseItem<string>(item.DataJson(), item.Name);
        }
    }
}

/// <summary>A question: its text (1 to 2 000 characters) and what it does (<c>new</c>, <c>regenerate</c> or <c>edit</c>).</summary>
public sealed class AskQuestionRequest
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(Message.MaxQuestionLength, MinimumLength = 1)]
    public string Text { get; set; } = "";

    [Required]
    public AskMode? Mode { get; set; }
}
