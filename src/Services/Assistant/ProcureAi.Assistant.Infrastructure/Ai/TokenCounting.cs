using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Application.Guardrails;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// The counting stage of every chat pipeline: each call is charged to the daily total of the tenant of the flow (<see cref="AiUsageScope"/>),
/// whatever the task (answer, rewrite, classifier, scan, chunk context, rerank). Before the call an estimate is reserved
/// (<see cref="TokenEstimator"/>: the prompt size plus the call's output limit); after it the reservation is settled with the input and
/// output tokens the provider reported (for a stream, in its last updates). A call that ends without usage is settled by
/// <see cref="TokenCounting.OnFailure"/>: kept when the provider may have billed it, the prompt part only when the provider evaluated the
/// prompt and refused it (a 400, Azure's prompt content filter), given back only when it certainly processed nothing. A Redis failure is
/// logged and the call goes on.
/// </summary>
internal sealed class TokenCountingChatClient(IChatClient inner, ITenantTokenBudget budget, TokenEstimator estimator, ILogger logger)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var estimate = estimator.ForChat(list, options);
        var reservation = await TokenCounting.ReserveAsync(budget, logger, tenant, estimate.Total);
        long? used = null;
        try
        {
            var response = await base.GetResponseAsync(list, options, cancellationToken);
            used = response.Usage is { } usage ? TokenCounting.Total(usage) : null;
            return response;
        }
        catch (Exception exception)
        {
            used = TokenCounting.Charge(TokenCounting.OnFailure(exception, cancellationToken), estimate);
            throw;
        }
        finally
        {
            await TokenCounting.SettleAsync(budget, logger, tenant, reservation, used);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var estimate = estimator.ForChat(list, options);
        var reservation = await TokenCounting.ReserveAsync(budget, logger, tenant, estimate.Total);
        long? tokens = null;
        long? failed = null;
        var started = false;
        try
        {
            await using var updates = base.GetStreamingResponseAsync(list, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool more;
                try
                {
                    more = await updates.MoveNextAsync();
                }
                catch (Exception exception) when (!started)
                {
                    // Refused before anything was streamed: charged by the kind of refusal. Once it started, a failure keeps it all.
                    failed = TokenCounting.Charge(TokenCounting.OnFailure(exception, cancellationToken), estimate);
                    throw;
                }

                if (!more)
                {
                    break;
                }

                started = true;
                foreach (var usage in updates.Current.Contents.OfType<UsageContent>())
                {
                    tokens = (tokens ?? 0) + TokenCounting.Total(usage.Details);
                }

                yield return updates.Current;
            }
        }
        finally
        {
            // A stream that started and gave no usage (the client left, a timeout, an error mid-stream) keeps its reservation.
            await TokenCounting.SettleAsync(budget, logger, tenant, reservation, tokens ?? failed);
        }
    }
}

/// <summary>The counting stage of the embedding generator: reserve the input's estimate, settle with the reported input tokens.</summary>
internal sealed class TokenCountingEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner, ITenantTokenBudget budget, TokenEstimator estimator, ILogger logger)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var list = values as IList<string> ?? [.. values];
        var estimate = estimator.ForEmbeddings(list);
        var reservation = await TokenCounting.ReserveAsync(budget, logger, tenant, estimate.Total);
        long? used = null;
        try
        {
            var embeddings = await base.GenerateAsync(list, options, cancellationToken);
            used = embeddings.Usage?.InputTokenCount;
            return embeddings;
        }
        catch (Exception exception)
        {
            used = TokenCounting.Charge(TokenCounting.OnFailure(exception, cancellationToken), estimate);
            throw;
        }
        finally
        {
            await TokenCounting.SettleAsync(budget, logger, tenant, reservation, used);
        }
    }
}

/// <summary>A call's reserved estimate: the prompt and the most it may produce (its output limit).</summary>
internal readonly record struct TokenEstimate(long Prompt, long Output)
{
    public long Total => Prompt + Output;
}

/// <summary>
/// The token estimate reserved before a call: the characters of everything sent (instructions, messages, tool calls and results, tool
/// declarations) divided by <c>AI:CharsPerTokenEstimate</c>, plus the call's output limit (<c>MaxOutputTokens</c>, set by the pipeline's
/// default-limits stage). It only has to be in the right range: the reservation is replaced by the real usage once the call reports it.
/// </summary>
internal sealed class TokenEstimator(int charsPerToken)
{
    public TokenEstimate ForChat(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        long chars = options?.Instructions?.Length ?? 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                chars += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    TextReasoningContent reasoning => reasoning.Text?.Length ?? 0,
                    FunctionCallContent call => call.Name.Length
                        + (call.Arguments?.Sum(a => (long)a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0),
                    FunctionResultContent result => result.Result?.ToString()?.Length ?? 0,
                    _ => 0,
                };
            }
        }

        foreach (var tool in options?.Tools ?? [])
        {
            chars += tool.Name.Length + (tool.Description?.Length ?? 0)
                + (tool is AIFunctionDeclaration function ? function.JsonSchema.GetRawText().Length : 0);
        }

        return new TokenEstimate(Tokens(chars), options?.MaxOutputTokens ?? 0);
    }

    public TokenEstimate ForEmbeddings(IEnumerable<string> values) => new(Tokens(values.Sum(v => (long)v.Length)), 0);

    private long Tokens(long chars) => (chars + charsPerToken - 1) / charsPerToken;
}

/// <summary>What a failed call that reported no usage is charged (<see cref="TokenCounting.OnFailure"/>).</summary>
internal enum FailedCallCharge
{
    /// <summary>The whole reservation: the provider may have processed and billed the call.</summary>
    Keep,

    /// <summary>The prompt part of the estimate: the provider evaluated the prompt (billed) and generated nothing.</summary>
    PromptOnly,

    /// <summary>Nothing: the call certainly reached no model.</summary>
    Refund,
}

internal static class TokenCounting
{
    /// <summary>
    /// How a call that failed without reporting usage is charged. Azure bills a call whenever it processed it, also with an error status:
    /// "a 400 error due to a content filter or input limit, or a 408 error due to a time-out" is charged, "a 401 error due to
    /// authentication or a 429 error due to exceeding the Rate Limit" is not (Azure OpenAI FAQ, "Why are failed API calls generating a
    /// charge?"; a prompt-filtered 400 is "charged for prompt evaluation").
    /// <list type="bullet">
    /// <item><see cref="FailedCallCharge.Refund"/>: the circuit breaker or another resilience stage refused it
    /// (<see cref="ExecutionRejectedException"/>), the provider answered 401, 403, 404 or 429, or no connection was ever made.</item>
    /// <item><see cref="FailedCallCharge.PromptOnly"/>: a 400 of any kind, including Azure's prompt content filter (which
    /// <see cref="ProviderErrors"/> turns into an input-stage <see cref="GuardBlockedException"/>).</item>
    /// <item><see cref="FailedCallCharge.Keep"/>: everything else, and always when the caller cancelled (the request may be with the
    /// provider already): timeouts, 408, 5xx, other 4xx, connections that broke after the request was sent, unknown errors.</item>
    /// </list>
    /// </summary>
    public static FailedCallCharge OnFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return FailedCallCharge.Keep;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ExecutionRejectedException:
                case ClientResultException { Status: 401 or 403 or 404 or 429 }:
                case HttpRequestException
                {
                    HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                        or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError,
                }:
                    return FailedCallCharge.Refund;
                case ClientResultException { Status: 400 }:
                case GuardBlockedException { Stage: GuardBlockedException.InputStage }:
                    return FailedCallCharge.PromptOnly;
            }
        }

        return FailedCallCharge.Keep;
    }

    /// <summary>The tokens to settle a failed call with; null keeps the reservation.</summary>
    public static long? Charge(FailedCallCharge charge, TokenEstimate estimate) => charge switch
    {
        FailedCallCharge.Refund => 0,
        FailedCallCharge.PromptOnly => estimate.Prompt,
        _ => null,
    };

    public static long Total(UsageDetails usage) => (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0);

    /// <summary>Reserves the estimate on the tenant's total; null without a tenant, or when Redis failed (the usage is then added after the call).</summary>
    public static async Task<TokenReservation?> ReserveAsync(ITenantTokenBudget budget, ILogger logger, string? tenant, long estimate)
    {
        if (tenant is null)
        {
            return null;
        }

        try
        {
            return await budget.ReserveAsync(tenant, estimate, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The daily token total of tenant {TenantId} could not be updated", tenant);
            return null;
        }
    }

    /// <summary>
    /// Settles the reservation with the used tokens (0 gives it back), or keeps it when <paramref name="usedTokens"/> is null (nothing
    /// reported, may have been billed). Without a reservation the used tokens are added.
    /// </summary>
    public static async Task SettleAsync(
        ITenantTokenBudget budget, ILogger logger, string? tenant, TokenReservation? reservation, long? usedTokens)
    {
        if (tenant is null || usedTokens is not { } used)
        {
            return;
        }

        try
        {
            if (reservation is not null)
            {
                await budget.SettleAsync(reservation, used, CancellationToken.None);
            }
            else
            {
                await budget.AddAsync(tenant, used, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The daily token total of tenant {TenantId} could not be updated", tenant);
        }
    }
}
