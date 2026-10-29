using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>The <c>Assistant:Chunking</c> section: sizes are estimated tokens, <c>ceil(chars / 4)</c> (plan 05 ruling 5).</summary>
public sealed class ChunkingOptions : IValidatableObject
{
    /// <summary>The smallest a child is when a section has to be split.</summary>
    [Range(50, 4_000)]
    public int MinTokens { get; set; } = 300;

    /// <summary>The largest a child may be, except a table, which stays whole. A section under this is a single child.</summary>
    [Range(100, 8_000)]
    public int MaxTokens { get; set; } = 800;

    /// <summary>How much of the previous child's end the next child repeats, as a percentage of <see cref="MaxTokens"/>.</summary>
    [Range(0, 40)]
    public int OverlapPercent { get; set; } = 12;

    /// <summary>
    /// The most a parent may hold. A section over this becomes several parents, each a window of whole children (a PDF whose headings were
    /// not detected is one huge section).
    /// </summary>
    [Range(100, 32_000)]
    public int MaxParentTokens { get; set; } = 2_000;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxParentTokens < MaxTokens)
        {
            yield return new ValidationResult(
                $"'Assistant:Chunking:{nameof(MaxParentTokens)}' must be at least '{nameof(MaxTokens)}' ({MaxParentTokens} < {MaxTokens}).",
                [nameof(MaxParentTokens), nameof(MaxTokens)]);
        }

        // With max < 2 * min the last child of a split could not reach the minimum.
        if (MaxTokens < 2 * MinTokens)
        {
            yield return new ValidationResult(
                $"'Assistant:Chunking:{nameof(MaxTokens)}' must be at least twice '{nameof(MinTokens)}' ({MaxTokens} < {2 * MinTokens}).",
                [nameof(MaxTokens), nameof(MinTokens)]);
        }
    }
}
