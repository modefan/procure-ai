using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>Which classifier runs after the patterns (ruling 11).</summary>
public enum GuardClassifier
{
    /// <summary>The classifier prompt on the <c>utility</c> client (<c>#task: classify</c>, model <c>AI:Guard:Model</c>, default the chat model).</summary>
    Model,

    /// <summary>Patterns only: no model call per question or document.</summary>
    None,
}

/// <summary>The <c>Assistant:Guard</c> section: the input and output guards of the Assistant (ruling 11). The classifier model is <c>AI:Guard:Model</c>.</summary>
public sealed class GuardOptions
{
    public const string SectionPath = AssistantOptions.SectionName + ":Guard";

    /// <summary>The classifier after the patterns: <c>Model</c> (default) or <c>None</c>.</summary>
    public GuardClassifier Classifier { get; set; } = GuardClassifier.Model;

    /// <summary>The longest question the guard lets through (2 000 characters, the same as the message limit).</summary>
    [Range(100, 20_000)]
    public int MaxQuestionLength { get; set; } = 2_000;

    /// <summary>How much of the streamed answer the output guard keeps from earlier updates, so a secret split across updates is caught.</summary>
    [Range(50, 4_000)]
    public int OutputWindow { get; set; } = 200;
}
