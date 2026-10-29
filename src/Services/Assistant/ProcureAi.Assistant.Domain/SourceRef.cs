namespace ProcureAi.Assistant.Domain;

/// <summary>A source an answer was given, which the answer cites as <see cref="Ref"/> (<c>[S1]</c>).</summary>
/// <param name="Ref">The marker in the prompt and the answer: <c>S1</c>, <c>S2</c>, …</param>
/// <param name="ChunkId">The child chunk that matched.</param>
/// <param name="Title">The policy document's or the purchase request's title.</param>
/// <param name="SectionPath">The headings of a policy chunk; empty for a purchase.</param>
/// <param name="Reference">The purchase request's number (PR-0982); null for a policy chunk.</param>
public sealed record SourceRef(string Ref, Guid ChunkId, SourceType Type, string Title, IReadOnlyList<string> SectionPath, string? Reference);
