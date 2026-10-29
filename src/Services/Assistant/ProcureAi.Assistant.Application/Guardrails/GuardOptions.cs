using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>Which classifier runs after the patterns (ruling 11).</summary>
public enum GuardClassifier
{
    /// <summary>The classifier prompt on the <c>guard</c> client (<c>#task: classify</c>, model <c>AI:Guard:Model</c>, default the chat model).</summary>
    Model,

    /// <summary>Patterns only: no model call per question or document.</summary>
    None,
}

/// <summary>The <c>Assistant:Guard</c> section: the input and output guards of the Assistant (ruling 11). The classifier model is <c>AI:Guard:Model</c>.</summary>
public sealed class GuardOptions : IValidatableObject
{
    public const string SectionPath = AssistantOptions.SectionName + ":Guard";

    /// <summary>The classifier after the patterns: <c>Model</c> (default) or <c>None</c>.</summary>
    public GuardClassifier Classifier { get; set; } = GuardClassifier.Model;

    /// <summary>The longest question the guard lets through (2 000 characters, the same as the message limit).</summary>
    [Range(100, 20_000)]
    public int MaxQuestionLength { get; set; } = 2_000;

    /// <summary>
    /// How much already seen answer text the output guard checks with each update, so a secret split across updates is caught. At least
    /// <see cref="HoldBackChars"/>.
    /// </summary>
    [Range(50, 4_000)]
    public int OutputWindow { get; set; } = 200;

    /// <summary>
    /// How many characters of streamed answer text the guard keeps back before passing them on (64). A secret is detected by the time its
    /// match is this long, so not one character of it reaches the client. At least <see cref="SecretPatterns.LongestBoundedMatch"/>.
    /// </summary>
    [Range(1, 4_000)]
    public int HoldBackChars { get; set; } = 64;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HoldBackChars < SecretPatterns.LongestBoundedMatch)
        {
            yield return new ValidationResult(
                $"'{SectionPath}:{nameof(HoldBackChars)}' must be at least {SecretPatterns.LongestBoundedMatch} (the longest secret pattern match); it is {HoldBackChars}.",
                [nameof(HoldBackChars)]);
        }

        if (OutputWindow < HoldBackChars)
        {
            yield return new ValidationResult(
                $"'{SectionPath}:{nameof(OutputWindow)}' must be at least '{SectionPath}:{nameof(HoldBackChars)}' ({HoldBackChars}); it is {OutputWindow}.",
                [nameof(OutputWindow)]);
        }
    }
}
