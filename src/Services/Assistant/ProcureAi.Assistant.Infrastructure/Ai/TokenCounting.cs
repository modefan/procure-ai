using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// The counting stage of every chat pipeline: each call is charged to the daily total of the tenant of the flow (<see cref="AiUsageScope"/>),
/// whatever the task (answer, rewrite, classifier, scan, chunk context, rerank). Before the call an estimate is reserved
/// (<see cref="TokenEstimator"/>: the prompt size plus the call's output limit); after it the reservation is settled with the input and
/// output tokens the provider reported (for a stream, in its last updates). A call that reports no usage, because the client left mid-stream
/// or the provider failed, keeps its reservation: the provider may have billed it. A Redis failure is logged and the call goes on.
/// </summary>
internal sealed class TokenCountingChatClient(IChatClient inner, ITenantTokenBudget budget, TokenEstimator estimator, ILogger logger)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var reservation = await TokenCounting.ReserveAsync(budget, logger, tenant, estimator.ForChat(list, options));
        var response = await base.GetResponseAsync(list, options, cancellationToken);
        await TokenCounting.SettleAsync(budget, logger, tenant, reservation, response.Usage is { } usage ? TokenCounting.Total(usage) : null);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var reservation = await TokenCounting.ReserveAsync(budget, logger, tenant, estimator.ForChat(list, options));
        long? tokens = null;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
            {
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    tokens = (tokens ?? 0) + TokenCounting.Total(usage.Details);
                }

                yield return update;
            }
        }
        finally
        {
            await TokenCounting.SettleAsync(budget, logger, tenant, reservation, tokens);
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
        var reservation = await TokenCounting.ReserveAsync(budget, logger, tenant, estimator.ForEmbeddings(list));
        var embeddings = await base.GenerateAsync(list, options, cancellationToken);
        await TokenCounting.SettleAsync(budget, logger, tenant, reservation, embeddings.Usage?.InputTokenCount);
        return embeddings;
    }
}

/// <summary>
/// The token estimate reserved before a call: the characters of everything sent (instructions, messages, tool calls and results, tool
/// declarations) divided by <c>AI:CharsPerTokenEstimate</c>, plus the call's output limit (<c>MaxOutputTokens</c>, set by the pipeline's
/// default-limits stage). It only has to be in the right range: the reservation is replaced by the real usage once the call reports it.
/// </summary>
internal sealed class TokenEstimator(int charsPerToken)
{
    public long ForChat(IEnumerable<ChatMessage> messages, ChatOptions? options)
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

        return Tokens(chars) + (options?.MaxOutputTokens ?? 0);
    }

    public long ForEmbeddings(IEnumerable<string> values) => Tokens(values.Sum(v => (long)v.Length));

    private long Tokens(long chars) => (chars + charsPerToken - 1) / charsPerToken;
}

internal static class TokenCounting
{
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
    /// Settles the reservation with the reported usage, or keeps it when none was reported. Without a reservation the usage is added.
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
