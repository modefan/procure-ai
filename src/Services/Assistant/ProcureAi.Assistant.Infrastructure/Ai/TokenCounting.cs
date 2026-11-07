using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;

namespace ProcureAi.Assistant.Infrastructure.Ai;

/// <summary>
/// The counting stage of every chat pipeline: the input and output tokens of each call are added to the daily total of the tenant of the
/// flow (<see cref="AiUsageScope"/>), whatever the task (answer, rewrite, classifier, scan, chunk context, rerank). A streamed call counts
/// the usage its last updates report. A Redis failure is logged and the call goes on: the cap undercounts rather than failing an answer.
/// </summary>
internal sealed class TokenCountingChatClient(IChatClient inner, ITenantTokenBudget budget, ILogger logger) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        await TokenCounting.AddAsync(budget, logger, tenant, (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        long tokens = 0;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    tokens += (usage.Details.InputTokenCount ?? 0) + (usage.Details.OutputTokenCount ?? 0);
                }

                yield return update;
            }
        }
        finally
        {
            await TokenCounting.AddAsync(budget, logger, tenant, tokens);
        }
    }
}

/// <summary>The counting stage of the embedding generator: the input tokens of each call (<see cref="TokenCountingChatClient"/>).</summary>
internal sealed class TokenCountingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, ITenantTokenBudget budget, ILogger logger)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tenant = AiUsageScope.Tenant;
        var embeddings = await base.GenerateAsync(values, options, cancellationToken);
        await TokenCounting.AddAsync(budget, logger, tenant, embeddings.Usage?.InputTokenCount ?? 0);
        return embeddings;
    }
}

internal static class TokenCounting
{
    public static async Task AddAsync(ITenantTokenBudget budget, ILogger logger, string? tenant, long tokens)
    {
        if (tenant is null || tokens <= 0)
        {
            return;
        }

        try
        {
            await budget.AddAsync(tenant, tokens, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The daily token total of tenant {TenantId} could not be updated", tenant);
        }
    }
}
