using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.Application.Info;

/// <summary>What the assistant screen shows about the model: which provider and model answer, whether it is the demo model, the daily quota.</summary>
/// <param name="Provider"><c>Fake</c> or <c>OpenAI</c>.</param>
/// <param name="ChatModel">The model that answers (<c>fake-chat</c> under the Fake provider).</param>
/// <param name="DemoModel">True under the Fake provider: the UI labels it "Demo model".</param>
/// <param name="QuestionsPerDay">The caller's daily question quota.</param>
public sealed record AssistantInfo(string Provider, string ChatModel, bool DemoModel, int QuestionsPerDay)
{
    public static AssistantInfo From(AiOptions ai, AssistantOptions assistant) =>
        new(ai.Provider.GetValueOrDefault().ToString(), ai.EffectiveChatModel, ai.IsFake, assistant.Quota.QuestionsPerDay);
}
