using System.Diagnostics.Metrics;
using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.Application.Telemetry;

/// <summary>
/// The Assistant's business metrics on the meter <c>ProcureAi.Assistant</c> (exported with every <c>ProcureAi.*</c> meter by the
/// service defaults). The meter comes from <see cref="IMeterFactory"/>, so each host (and test host) has its own. Tags carry IDs and
/// names only, never prompt or answer text.
/// </summary>
public sealed class AssistantMetrics
{
    public const string MeterName = "ProcureAi.Assistant";
    public const string Tokens = "assistant.tokens";
    public const string CostUsd = "assistant.cost_usd";
    public const string ToolCalls = "assistant.tool_calls";
    public const string GuardBlocks = "assistant.guard_blocks";
    public const string TimeToFirstToken = "assistant.time_to_first_token";
    public const string Questions = "assistant.questions";

    private const decimal PerMillion = 1_000_000m;

    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _guardBlocks;
    private readonly Histogram<double> _timeToFirstToken;
    private readonly Counter<long> _questions;

    public AssistantMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _tokens = meter.CreateCounter<long>(Tokens, unit: "{token}", description: "Model tokens used, by model, kind (input/output) and tenant.");
        _cost = meter.CreateCounter<double>(CostUsd, unit: "USD", description: "Estimated model cost from the configured prices (AI:Prices).");
        _toolCalls = meter.CreateCounter<long>(ToolCalls, unit: "{call}", description: "Tool calls the model made, by tool and outcome.");
        _guardBlocks = meter.CreateCounter<long>(GuardBlocks, unit: "{block}", description: "Questions or answers a guard blocked, by stage and reason.");
        _timeToFirstToken = meter.CreateHistogram<double>(TimeToFirstToken, unit: "ms", description: "From the question to the first streamed answer text.");
        _questions = meter.CreateCounter<long>(Questions, unit: "{question}", description: "Questions asked, by outcome.");
    }

    /// <summary>Counts a chat call's tokens and its estimated cost.</summary>
    public void ChatUsage(string model, string tenantId, long inputTokens, long outputTokens, PriceOptions prices)
    {
        _tokens.Add(inputTokens, Tag("model", model), Tag("kind", "input"), Tag("tenant", tenantId));
        _tokens.Add(outputTokens, Tag("model", model), Tag("kind", "output"), Tag("tenant", tenantId));
        var cost = (inputTokens * prices.InputPerMillionUsd + outputTokens * prices.OutputPerMillionUsd) / PerMillion;
        _cost.Add((double)cost, Tag("model", model), Tag("tenant", tenantId));
    }

    /// <summary>Counts an embedding call's input tokens and its estimated cost.</summary>
    public void EmbeddingUsage(string model, string tenantId, long inputTokens, PriceOptions prices)
    {
        _tokens.Add(inputTokens, Tag("model", model), Tag("kind", "input"), Tag("tenant", tenantId));
        _cost.Add((double)(inputTokens * prices.EmbeddingPerMillionUsd / PerMillion), Tag("model", model), Tag("tenant", tenantId));
    }

    /// <param name="outcome"><c>ok</c>, <c>error</c>, <c>timeout</c> or <c>denied</c>.</param>
    public void ToolCall(string tool, string outcome) => _toolCalls.Add(1, Tag("tool", tool), Tag("outcome", outcome));

    /// <param name="stage"><c>input</c>, <c>output</c> or <c>document</c>.</param>
    public void GuardBlock(string stage, string reason) => _guardBlocks.Add(1, Tag("stage", stage), Tag("reason", reason));

    public void FirstToken(TimeSpan elapsed) => _timeToFirstToken.Record(elapsed.TotalMilliseconds);

    /// <param name="outcome"><c>answered</c>, <c>blocked</c>, <c>stopped</c>, <c>failed</c> or <c>over_quota</c>.</param>
    public void Question(string outcome) => _questions.Add(1, Tag("outcome", outcome));

    private static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);
}
