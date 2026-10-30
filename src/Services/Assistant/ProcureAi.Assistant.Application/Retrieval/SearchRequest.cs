using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>What to search for: the user's (rewritten) question, the source types to search and how many hits to return.</summary>
/// <param name="Query">Free text; any input is safe (quotes, operators, empty).</param>
/// <param name="Types">The source types to search (<see cref="SourceType.Policy"/>, <see cref="SourceType.Purchase"/>).</param>
/// <param name="TopK">How many hits at most; 0 or less takes <c>Assistant:Retrieval:TopK</c>.</param>
public sealed record SearchRequest(string Query, IReadOnlySet<SourceType> Types, int TopK)
{
    /// <summary>Both source types.</summary>
    public static readonly IReadOnlySet<SourceType> AllTypes = new HashSet<SourceType> { SourceType.Policy, SourceType.Purchase };

    public static readonly IReadOnlySet<SourceType> PolicyOnly = new HashSet<SourceType> { SourceType.Policy };

    public static readonly IReadOnlySet<SourceType> PurchasesOnly = new HashSet<SourceType> { SourceType.Purchase };
}
