using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Application.Tools;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Application.Chat;

/// <summary>
/// Answers a question in a conversation of the caller as a stream of <see cref="SseEvent"/>s (plan 05 Task 5a, rulings 6 to 10).
/// <see cref="StartAsync"/> does everything that can still be a plain HTTP answer: the conversation (404, also for another user's or
/// tenant's), the mode (422), the tenant's daily token cap (429), the per-conversation lock (409), the daily quota (429, counted here), then the question is stored. The stream
/// then rewrites a follow-up with the history (<see cref="QueryRewriter"/>), searches (<see cref="IHybridSearch"/>, top
/// <c>Assistant:Retrieval:TopK</c>) and sends <c>sources</c>, streams the <c>answer</c> client's text as <c>delta</c>s with the six read-only
/// tools offered (past purchases are searched, and <c>find_similar_purchases</c> offered, only when the caller has <c>requests.read</c>) (<see cref="AssistantTools"/>, Task 5b: each call is a <c>tool_call</c> and a <c>tool_result</c> event and a saved
/// <see cref="ToolStep"/>; a <c>search_policy</c> result that adds sources is followed by a further <c>sources</c> event with every source
/// so far), runs the grounding rule
/// (an answer that cites nothing is replaced: <c>replace</c>), saves the answer and sends <c>done</c>. Failures after the response started
/// are an <c>error</c> event: 422 when a guard blocked (<see cref="GuardBlockedException"/>), 503 when the provider failed or timed out.
/// <para>
/// The request's cancellation token reaches every model call; when the client leaves, what was streamed is saved as
/// <see cref="MessageStatus.Stopped"/>. The answer is saved and the lock released in a <c>finally</c>, so neither depends on how the stream
/// ended. One log line per answer carries IDs, model, tokens, duration, outcome and guard flags, never text.
/// </para>
/// </summary>
public sealed class AnswerStreamer(
    IConversationStore store,
    IAnswerLock locks,
    IQuestionQuota quota,
    ITenantTokenBudget tokenBudget,
    IHybridSearch search,
    QueryRewriter rewriter,
    AssistantToolsFactory toolsFactory,
    [FromKeyedServices(AiClients.Answer)] IChatClient answerClient,
    ICallerContext caller,
    ICallerPermissions permissions,
    ICacheInvalidator cache,
    IAiFailures failures,
    AssistantMetrics metrics,
    IOptions<AssistantOptions> options,
    IOptions<AiOptions> ai,
    TimeProvider time,
    ILogger<AnswerStreamer> logger)
{
    public const string NotFoundTitle = "The conversation was not found.";
    public const string BusyTitle = "An answer is already being written in this conversation.";
    public const string GuardTitle = "I can't help with that";
    public const string UnavailableTitle = "The assistant is unavailable right now.";
    public const string FailedTitle = "The assistant could not answer.";
    public const string NotSavedTitle = "The answer could not be saved.";

    /// <summary>What a tool step's summary or arguments show when they contain what the output guard calls a secret (<see cref="SecretPatterns"/>).</summary>
    public const string HiddenSummary = "(hidden)";

    /// <summary>What replaces an answer that cites none of its sources (ruling 10).</summary>
    public const string NotGroundedText =
        "I couldn't find this in the procurement policy or past purchases, so I can't answer it reliably. Ask your manager or finance.";

    /// <summary>How long saving an answer may take after the client left.</summary>
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(10);

    public static string QuotaTitle(int limit) => $"You've used all {limit} questions for today.";

    /// <summary>The 429 of a tenant whose daily token cap is reached (<c>AI:DailyTokenCapPerTenant</c>).</summary>
    public const string TokenCapTitle = "The assistant has reached today's limit for your organisation.";

    public const string TokenCapDetail = "It answers again after midnight. The policy documents stay available in the meantime.";

    public async Task<AnswerStart> StartAsync(AskQuestion command, CancellationToken cancellationToken)
    {
        var tenant = caller.TenantId ?? throw new ForbiddenException("Asking needs a tenant.");
        var owner = caller.UserId;
        if (await RefusalAsync(command, owner, cancellationToken) is { } early)
        {
            return early;
        }

        // The tenant's daily token cap (a cost guard): checked before anything is counted, so a refused question costs no quota. The
        // tokens themselves are counted by every model pipeline (its counting stage), for the tenant of the request (AiUsageScope).
        var budget = await tokenBudget.GetAsync(tenant, cancellationToken);
        if (budget.Spent)
        {
            metrics.Question("over_token_cap");
            var untilMidnight = budget.ResetsAt - time.GetUtcNow();
            return new AnswerStart.Refused(429, TokenCapTitle, TokenCapDetail, untilMidnight > TimeSpan.Zero ? untilMidnight : TimeSpan.Zero);
        }

        // Past purchases are request data: searched, and their tool offered, only with requests.read in the runtime matrix.
        var purchasesAllowed = await permissions.HasAsync(Permissions.RequestsRead, cancellationToken);

        var held = await locks.TryAcquireAsync(tenant, command.ConversationId, cancellationToken);
        if (held is null)
        {
            return new AnswerStart.Refused(409, BusyTitle, "Wait for it to finish, or stop it, and ask again.");
        }

        try
        {
            // Read again under the lock: an answer that just ended may have changed it.
            var conversation = await store.FindAsync(command.ConversationId, owner, cancellationToken);
            if (conversation is null)
            {
                await held.DisposeAsync();
                return new AnswerStart.Refused(404, NotFoundTitle);
            }

            if (conversation.CannotAsk(command.Mode) is { } reason)
            {
                await held.DisposeAsync();
                return new AnswerStart.Refused(422, reason);
            }

            var count = await quota.TryCountAsync(tenant, owner, cancellationToken);
            if (!count.Allowed)
            {
                await held.DisposeAsync();
                metrics.Question("over_quota");
                var wait = count.ResetsAt - time.GetUtcNow();
                return new AnswerStart.Refused(429, QuotaTitle(count.Limit), "The count starts again at midnight.", wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
            }

            var now = time.GetUtcNow();
            var question = command.Mode switch
            {
                AskMode.Regenerate => conversation.Regenerate(now),
                AskMode.Edit => conversation.EditLastQuestion(Guid.CreateVersion7(), command.Text, now),
                _ => conversation.Ask(Guid.CreateVersion7(), command.Text, now),
            };
            if (!await store.SaveAsync(conversation, cancellationToken))
            {
                // Deleted between the read and the save: nothing to answer, and the question is not counted.
                await quota.ReturnAsync(tenant, owner, CancellationToken.None);
                await held.DisposeAsync();
                return new AnswerStart.Refused(404, NotFoundTitle);
            }

            await InvalidateAsync(conversation.Id);
            return new AnswerStart.Streaming(StreamAsync(conversation, question, purchasesAllowed, held, cancellationToken), held);
        }
        catch
        {
            await held.DisposeAsync();
            throw;
        }
    }

    private async Task<AnswerStart.Refused?> RefusalAsync(AskQuestion command, string owner, CancellationToken cancellationToken)
    {
        var conversation = await store.FindAsync(command.ConversationId, owner, cancellationToken);
        return conversation is null ? new AnswerStart.Refused(404, NotFoundTitle)
            : conversation.CannotAsk(command.Mode) is { } reason ? new AnswerStart.Refused(422, reason)
            : null;
    }

    /// <summary>
    /// The stream around <see cref="AnswerAsync"/>: an exception that escapes it (a bug outside the model calls) ends the answer as
    /// <see cref="MessageStatus.Failed"/> with an <c>error</c> event instead of a broken stream; a client that left gets what was streamed
    /// saved as <see cref="MessageStatus.Stopped"/>. The lock is released here at the latest (the answer releases it before its last event).
    /// </summary>
    private async IAsyncEnumerable<SseEvent> StreamAsync(
        Conversation conversation, Message question, bool purchasesAllowed, IAsyncDisposable held, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = new AnswerState(Guid.CreateVersion7(), Stopwatch.GetTimestamp(), ai.Value.EffectiveChatModel);
        try
        {
            await using var events = AnswerAsync(conversation, question, purchasesAllowed, answer, held, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                var (more, failure) = await TryAsync(async () => await events.MoveNextAsync());
                if (failure is not null)
                {
                    if (!answer.Saved && await FailAsync(answer, conversation, failure, cancellationToken) is { } error)
                    {
                        await held.DisposeAsync();
                        yield return error;
                    }

                    yield break;
                }

                if (!more)
                {
                    yield break;
                }

                yield return events.Current;
            }
        }
        finally
        {
            // The client left (the stream was disposed while suspended): what was streamed is kept.
            if (!answer.Saved)
            {
                answer.Status ??= MessageStatus.Stopped;
                await SaveAsync(answer, conversation);
            }

            await held.DisposeAsync();
        }
    }

    private async IAsyncEnumerable<SseEvent> AnswerAsync(
        Conversation conversation, Message question, bool purchasesAllowed, AnswerState answer, IAsyncDisposable held,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var history = conversation.HistoryBeforeLastQuestion(options.Value.Answer.HistoryMessages);

        // 1. Retrieval: a follow-up is rewritten with the history first (its tokens are metered like the answer's).
        var (hits, failure) = await TryAsync(async () =>
        {
            var rewrite = await rewriter.RewriteAsync(history, question.Text, cancellationToken);
            if (rewrite.Model is not null || rewrite.Usage != TokenUsage.None)
            {
                metrics.ChatUsage(
                    rewrite.Model ?? ai.Value.EffectiveChatModel, conversation.TenantId, rewrite.Usage.Input, rewrite.Usage.Output,
                    ai.Value.EffectivePrices);
            }

            var types = purchasesAllowed ? SearchRequest.AllTypes : SearchRequest.PolicyOnly;
            return await search.SearchAsync(new SearchRequest(rewrite.Query, types, options.Value.Retrieval.TopK), cancellationToken);
        });
        if (failure is not null)
        {
            if (await FailAsync(answer, conversation, failure, cancellationToken) is { } error)
            {
                await held.DisposeAsync();
                yield return error;
            }

            yield break;
        }

        answer.Sources = [.. hits!.Select((hit, i) => new SourceRef($"S{i + 1}", hit.ChunkId, hit.Type, hit.Title, hit.SectionPath, hit.Reference))];
        yield return SseEvent.OfSources(answer.Sources);

        // The cap again, right before the expensive calls (the guard classifier and the answer): the rewrite and the query embedding,
        // or other questions of the tenant meanwhile, may have reached it since the check at the start. Like that check, a refusal here
        // costs no quota: the question counted at the start is given back.
        if (await TokenCapReachedAsync(conversation.TenantId, cancellationToken))
        {
            metrics.Question("over_token_cap");
            await quota.ReturnAsync(conversation.TenantId, conversation.UserId, CancellationToken.None);
            answer.Status = MessageStatus.Failed;
            await SaveAsync(answer, conversation);
            await held.DisposeAsync();
            yield return SseEvent.OfError(429, TokenCapTitle, TokenCapDetail);
            yield break;
        }

        // 2. The answer, streamed, with the tools of this question (they act as the caller and number new sources on from these).
        var prompt = PromptBuilder.Build(hits!, answer.Sources, history, question.Text);
        var tools = toolsFactory.Create(answer.Sources, purchasesAllowed);
        answer.Tools = tools.Session;
        var chatOptions = new ChatOptions
        {
            Tools = [.. tools.Functions],
            // Our conversation ID for the guard's log only; never ChatOptions.ConversationId (a provider-side conversation).
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [GuardrailChatClient.ConversationIdProperty] = conversation.Id.ToString(),
                [GuardrailChatClient.InputCheckProperty] = answer.InputCheck,
            },
        };
        await using (var updates = answerClient.GetStreamingResponseAsync(prompt, chatOptions, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                var (more, streamFailure) = await TryAsync(async () => await updates.MoveNextAsync());
                if (streamFailure is not null)
                {
                    if (await FailAsync(answer, conversation, streamFailure, cancellationToken) is { } error)
                    {
                        await held.DisposeAsync();
                        yield return error;
                    }

                    yield break;
                }

                if (!more)
                {
                    break;
                }

                foreach (var item in answer.Take(updates.Current))
                {
                    if (item.Name == SseEvent.Delta && answer.FirstTokenTicks is null)
                    {
                        answer.FirstTokenTicks = Stopwatch.GetTimestamp();
                        metrics.FirstToken(Stopwatch.GetElapsedTime(answer.StartTicks));
                    }

                    yield return item;
                }
            }
        }

        // 3. The grounding rule (ruling 10): an answer that cites none of its sources and used no tool is replaced.
        answer.Status = MessageStatus.Complete;
        if (!OutputGuard.IsGrounded(answer.Text.ToString(), answer.Sources.Count, answer.ToolSteps.Count(s => s.Ok)))
        {
            answer.Text.Clear().Append(NotGroundedText);
            answer.GuardFlags.Add($"{GuardBlockedException.OutputStage}:{GuardReasons.Ungrounded}");
            metrics.GuardBlock(GuardBlockedException.OutputStage, GuardReasons.Ungrounded);
            yield return SseEvent.OfReplace(NotGroundedText);
        }

        // 4. Saved, then the lock is free before the client hears "done" (it may ask the next question at once).
        var saved = await SaveAsync(answer, conversation);
        await held.DisposeAsync();
        yield return saved
            ? SseEvent.OfDone(answer.Id, answer.Usage, answer.Model)
            : SseEvent.OfError(500, NotSavedTitle, "Reload the conversation and ask again.");
    }

    /// <summary>Saves the failure and returns the event to send, or null when the client left (nothing can be sent).</summary>
    private async Task<SseEvent?> FailAsync(AnswerState answer, Conversation conversation, Exception failure, CancellationToken cancellationToken)
    {
        SseEvent? error;
        switch (failure)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                answer.Status = MessageStatus.Stopped;
                error = null;
                break;
            case GuardBlockedException blocked:
                if (blocked.Reason == GuardReasons.ProviderFilter)
                {
                    // Our guards passed it, the provider's content filter did not: worth a look, but not a failure of ours.
                    metrics.GuardBlock(blocked.Stage, blocked.Reason);
                    if (blocked.Stage == GuardBlockedException.InputStage)
                    {
                        logger.LogWarning(
                            "The model provider's content filter refused a question in conversation {ConversationId}", conversation.Id);
                    }
                    else
                    {
                        // Azure ends a filtered completion with finish_reason content_filter (ProviderErrorChatClient).
                        logger.LogWarning(
                            "The model provider's content filter refused an answer in conversation {ConversationId}", conversation.Id);
                    }
                }

                answer.Status = MessageStatus.Blocked;
                answer.GuardFlags.Add($"{blocked.Stage}:{blocked.Reason}");
                if (blocked.Stage == GuardBlockedException.InputStage)
                {
                    answer.Text.Clear();
                }

                error = SseEvent.OfError(422, GuardTitle, GuardReasons.Describe(blocked.Reason));
                break;
            case var _ when IsUnavailable(failure):
                answer.Status = MessageStatus.Failed;
                logger.LogWarning(failure, "The model provider failed during an answer in conversation {ConversationId}", conversation.Id);
                error = SseEvent.OfError(503, UnavailableTitle, "Try again in a moment.");
                break;
            default:
                answer.Status = MessageStatus.Failed;
                logger.LogError(failure, "An answer failed unexpectedly in conversation {ConversationId}", conversation.Id);
                error = SseEvent.OfError(500, FailedTitle, "Try again; if it keeps happening, tell an administrator.");
                break;
        }

        await SaveAsync(answer, conversation);
        return error;
    }

    /// <summary>Whether the tenant's daily token cap is reached; false when it cannot be read (the cap is a cost guard, not a gate).</summary>
    private async Task<bool> TokenCapReachedAsync(string tenant, CancellationToken cancellationToken)
    {
        try
        {
            return (await tokenBudget.GetAsync(tenant, cancellationToken)).Spent;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The daily token total of tenant {TenantId} could not be read before the answer", tenant);
            return false;
        }
    }

    /// <summary>A provider failure that may pass (unreachable, 429/5xx, a timeout).</summary>
    private bool IsUnavailable(Exception failure) =>
        failure is TimeoutException or OperationCanceledException || failures.IsTransient(failure);

    /// <summary>Saves the answer (also when the client left); false when it could not be stored or the conversation is gone.</summary>
    private async Task<bool> SaveAsync(AnswerState answer, Conversation conversation)
    {
        answer.Saved = true;
        var status = answer.Status ?? MessageStatus.Stopped;
        var latency = (long)Stopwatch.GetElapsedTime(answer.StartTicks).TotalMilliseconds;
        long? firstToken = answer.FirstTokenTicks is { } first ? (long)Stopwatch.GetElapsedTime(answer.StartTicks, first).TotalMilliseconds : null;
        var outcome = status switch
        {
            MessageStatus.Complete => "answered",
            MessageStatus.Blocked => "blocked",
            MessageStatus.Stopped => "stopped",
            _ => "failed",
        };

        metrics.Question(outcome);
        metrics.ChatUsage(answer.Model, conversation.TenantId, answer.Usage.Input, answer.Usage.Output, ai.Value.EffectivePrices);
        logger.LogInformation(
            "Answer {Outcome} in conversation {ConversationId} for user {UserId} of tenant {TenantId}: model {Model}, {InputTokens} input and"
            + " {OutputTokens} output tokens, {DurationMs} ms, guard flags {GuardFlags}",
            outcome, conversation.Id, conversation.UserId, conversation.TenantId, answer.Model, answer.Usage.Input, answer.Usage.Output, latency,
            answer.GuardFlags.Count == 0 ? "none" : string.Join(',', answer.GuardFlags));

        // Saved even when the client left, so a reload shows what was streamed.
        using var timeout = new CancellationTokenSource(SaveTimeout);
        var saved = false;
        try
        {
            conversation.Answer(
                new Message(
                    answer.Id, MessageRole.Assistant, answer.Text.ToString(), status, time.GetUtcNow(), answer.Sources, answer.ToolSteps,
                    answer.Usage, answer.Model, answer.GuardFlags, latency, firstToken, answer.InputCheck.Passed),
                time.GetUtcNow());
            saved = await store.SaveAsync(conversation, timeout.Token);
            if (!saved)
            {
                logger.LogInformation("Conversation {ConversationId} was deleted while it was answered; the answer is dropped", conversation.Id);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The answer in conversation {ConversationId} could not be saved", conversation.Id);
        }

        await InvalidateAsync(conversation.Id);
        return saved;
    }

    private async Task InvalidateAsync(Guid conversationId)
    {
        try
        {
            await cache.RemoveByTagAsync(ConversationCacheTag.Of(caller.TenantId, conversationId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for conversation {ConversationId}; cached reads expire on their own", conversationId);
        }
    }

    /// <summary>Runs <paramref name="step"/> and returns its result or its exception (an iterator cannot yield inside a try with a catch).</summary>
    private static async Task<(T? Result, Exception? Failure)> TryAsync<T>(Func<Task<T>> step)
    {
        try
        {
            return (await step(), null);
        }
        catch (Exception exception)
        {
            return (default, exception);
        }
    }

    /// <summary>What one answer has gathered so far.</summary>
    private sealed class AnswerState(Guid id, long startTicks, string model)
    {
        public Guid Id { get; } = id;

        public long StartTicks { get; } = startTicks;

        public long? FirstTokenTicks { get; set; }

        /// <summary>Marked by the guard once the question passed the input guard (<see cref="GuardrailChatClient.InputCheckProperty"/>).</summary>
        public InputCheck InputCheck { get; } = new();

        public string Model { get; private set; } = model;

        public StringBuilder Text { get; } = new();

        public IReadOnlyList<SourceRef> Sources { get; set; } = [];

        public List<ToolStep> ToolSteps { get; } = [];

        /// <summary>The tools' state of this question: the outcome of each call and the sources <c>search_policy</c> added.</summary>
        public ToolSession? Tools { get; set; }

        public List<string> GuardFlags { get; } = [];

        public TokenUsage Usage { get; private set; } = TokenUsage.None;

        public MessageStatus? Status { get; set; }

        public bool Saved { get; set; }

        private readonly Dictionary<string, (FunctionCallContent Call, string Arguments, long StartTicks)> _openCalls = new(StringComparer.Ordinal);

        /// <summary>The events of one model update; the text, usage, model and tool steps are recorded.</summary>
        public IEnumerable<SseEvent> Take(ChatResponseUpdate update)
        {
            if (!string.IsNullOrEmpty(update.ModelId))
            {
                Model = update.ModelId;
            }

            var events = new List<SseEvent>();
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        Text.Append(text.Text);
                        events.Add(SseEvent.OfDelta(text.Text));
                        break;
                    case UsageContent usage:
                        Usage = new TokenUsage(
                            Usage.Input + (usage.Details.InputTokenCount ?? 0), Usage.Output + (usage.Details.OutputTokenCount ?? 0));
                        break;
                    case FunctionCallContent call:
                        var json = Arguments(call);
                        _openCalls[call.CallId] = (call, json, Stopwatch.GetTimestamp());
                        events.Add(SseEvent.OfToolCall(call.CallId, call.Name, JsonSerializer.Deserialize<JsonElement>(json)));
                        break;
                    case FunctionResultContent result:
                        events.AddRange(ToolResult(result));
                        break;
                }
            }

            return events;
        }

        /// <summary>
        /// The <c>tool_result</c> of a finished call, from the tool's own outcome (<see cref="ToolSession"/>: ok, summary, how long the call
        /// took); a call the tool never ran (unknown tool, arguments that did not bind) failed. The summary goes to the browser, so it is
        /// checked like the answer's text and shown as <see cref="HiddenSummary"/> on a hit. Sources added by the call follow as a
        /// <c>sources</c> event with every source so far (the client replaces its list).
        /// </summary>
        private IEnumerable<SseEvent> ToolResult(FunctionResultContent result)
        {
            var (name, arguments, started) = _openCalls.Remove(result.CallId, out var open)
                ? (open.Call.Name, open.Arguments, open.StartTicks)
                : ("unknown", "{}", Stopwatch.GetTimestamp());
            var outcome = Tools?.TakeOutcome(result.CallId);
            var summary = outcome?.Summary ?? "The tool failed.";
            var step = new ToolStep(
                result.CallId, name, arguments, outcome?.Ok ?? false, SecretPatterns.Find(summary) is null ? summary : HiddenSummary,
                outcome?.DurationMs ?? (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            ToolSteps.Add(step);
            yield return SseEvent.OfToolResult(step);

            if (outcome?.AddedSources == true && Tools is not null)
            {
                Sources = Tools.Sources;
                yield return SseEvent.OfSources(Sources);
            }
        }

        /// <summary>The call's arguments as JSON for the event and the saved step; <c>{"hidden":true}</c> when they contain a secret.</summary>
        private static string Arguments(FunctionCallContent call)
        {
            var json = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
            return SecretPatterns.Find(json) is null ? json : "{\"hidden\":true}";
        }
    }
}
