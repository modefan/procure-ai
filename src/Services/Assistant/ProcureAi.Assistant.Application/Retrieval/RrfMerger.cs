namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>An item of the fused list with its reciprocal rank fusion score.</summary>
public sealed record RrfResult<T>(T Item, double Score);

/// <summary>
/// Reciprocal rank fusion of ranked lists (the search arms): each item scores <c>Σ 1 / (k + rank)</c> over the lists it is in (rank 1 for
/// the first), so an item that both arms rank well beats one that only one arm ranks first. Ranks, not raw scores, are combined, so
/// cosine distances and <c>ts_rank_cd</c> values never need to be put on one scale. A higher <c>k</c> flattens the difference between
/// the top ranks.
/// </summary>
public static class RrfMerger
{
    /// <summary>
    /// The items of <paramref name="arms"/>, best first. An item repeated within one arm counts at its best rank only. Equal scores keep
    /// a stable order: the item seen first (by arm, then by rank) comes first.
    /// </summary>
    public static IReadOnlyList<RrfResult<T>> Merge<T>(IEnumerable<IReadOnlyList<T>> arms, int k)
        where T : notnull
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        var scores = new Dictionary<T, double>();
        var firstSeen = new Dictionary<T, int>();
        foreach (var arm in arms)
        {
            var seenInArm = new HashSet<T>();
            for (var i = 0; i < arm.Count; i++)
            {
                var item = arm[i];
                if (!seenInArm.Add(item))
                {
                    continue;
                }

                scores[item] = scores.GetValueOrDefault(item) + 1.0 / (k + i + 1);
                firstSeen.TryAdd(item, firstSeen.Count);
            }
        }

        return [.. scores
            .OrderByDescending(s => s.Value)
            .ThenBy(s => firstSeen[s.Key])
            .Select(s => new RrfResult<T>(s.Key, s.Value))];
    }
}
