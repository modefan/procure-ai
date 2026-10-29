using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Telemetry;

namespace ProcureAi.Assistant.Application.Guardrails;

public static class ChatClientBuilderExtensions
{
    /// <summary>
    /// Adds the guard stage (<see cref="GuardrailChatClient"/>) through the registered <see cref="IAnswerGuardrail"/>. Call it first, so the
    /// guard is the outermost stage: it sees the caller's messages and options before anything else runs.
    /// </summary>
    public static ChatClientBuilder UseGuardrails(this ChatClientBuilder builder) =>
        builder.Use((inner, services) => services.GetRequiredService<IAnswerGuardrail>().Wrap(inner));
}

/// <summary>The real <see cref="IAnswerGuardrail"/>: wraps the rest of the answer pipeline in a <see cref="GuardrailChatClient"/>.</summary>
public sealed class AnswerGuardrail(
    IPromptShield shield, AssistantMetrics metrics, ILogger<GuardrailChatClient> logger, IOptions<AssistantOptions> options) : IAnswerGuardrail
{
    public IChatClient Wrap(IChatClient inner) => new GuardrailChatClient(inner, shield, metrics, logger, options.Value.Guard);
}
