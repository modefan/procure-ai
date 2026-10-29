using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>One retrieved chunk.</summary>
/// <param name="ChunkId">The child chunk that matched.</param>
/// <param name="Title">The policy document's title, or the purchase request's title.</param>
/// <param name="SectionPath">The headings of a policy chunk ("3 Approvals", "3.2 Thresholds"); empty for a purchase.</param>
/// <param name="ChildText">The matched chunk's text.</param>
/// <param name="ParentText">The text of its parent section, for the prompt; the child's own text when it has no parent (a purchase).</param>
/// <param name="Reference">The purchase request's number ("PR-0982"); null for a policy chunk.</param>
/// <param name="Score">The reciprocal rank fusion score (higher is better).</param>
public sealed record SearchHit(
    Guid ChunkId, SourceType Type, string Title, IReadOnlyList<string> SectionPath, string ChildText, string ParentText, string? Reference,
    double Score);
