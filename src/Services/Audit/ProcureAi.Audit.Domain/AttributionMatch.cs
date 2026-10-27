namespace ProcureAi.Audit.Domain;

/// <summary>One change Debezium delivered, as the match knows it.</summary>
/// <param name="EntryId">The interceptor row it was matched to; null while unmatched.</param>
public sealed record MatchedChange(string ChangeId, long Lsn, DateTimeOffset CommittedAt, Guid? EntryId);

/// <summary>One interceptor row, as the match knows it.</summary>
public sealed record MatchedEntry(Guid EntryId, long Lsn, DateTimeOffset OccurredAt);

/// <summary>
/// Everything Audit has seen of one row in one transaction: the changes Debezium delivered and the interceptor rows the app wrote
/// for it, keyed by <c>(database, transaction ID, table, key)</c>. Both kinds arrive on their own, in any order; whichever arrives
/// second completes the match (<see cref="Pair"/>). The store serialises arrivals through this one document (optimistic
/// concurrency), so two arrivals at the same moment cannot both miss each other.
/// <para>
/// The transaction ID is Postgres' 32-bit xid and wraps around, so a change and a row match only when the commit and the save are
/// at most the matching window apart. One transaction may change a row several times (and write one interceptor row each); the
/// changes and rows are then paired in log order (LSN), the n-th change with the n-th row; a change whose row has not arrived stays
/// pending until it does.
/// </para>
/// </summary>
public sealed record AttributionMatch(
    string Id, IReadOnlyList<MatchedChange> Changes, IReadOnlyList<MatchedEntry> Entries, DateTimeOffset LastSeenAt)
{
    /// <summary>The stored version (optimistic concurrency).</summary>
    public Guid Version { get; set; }

    public static string KeyOf(string database, long transactionId, string table, string primaryKey) =>
        $"{database}:{transactionId}:{table}:{primaryKey}";

    public static AttributionMatch Start(string id, DateTimeOffset seenAt) => new(id, [], [], seenAt);

    public AttributionMatch WithChange(string changeId, long lsn, DateTimeOffset committedAt) =>
        Changes.Any(c => c.ChangeId == changeId)
            ? this
            : this with { Changes = [.. Changes, new MatchedChange(changeId, lsn, committedAt, null)], LastSeenAt = Later(committedAt) };

    public AttributionMatch WithEntry(Guid entryId, long lsn, DateTimeOffset occurredAt) =>
        Entries.Any(e => e.EntryId == entryId)
            ? this
            : this with { Entries = [.. Entries, new MatchedEntry(entryId, lsn, occurredAt)], LastSeenAt = Later(occurredAt) };

    /// <summary>
    /// Matches every unmatched change to an interceptor row saved within <paramref name="window"/> of its commit. Returns the match
    /// with the pairs recorded and the pairs that are new.
    /// </summary>
    public (AttributionMatch Match, IReadOnlyList<(string ChangeId, Guid EntryId)> Paired) Pair(TimeSpan window)
    {
        var ordered = Changes.OrderBy(c => c.Lsn).ToList();
        var paired = new List<(string ChangeId, Guid EntryId)>();
        var changes = new List<MatchedChange>(ordered.Count);
        for (var rank = 0; rank < ordered.Count; rank++)
        {
            var change = ordered[rank];
            if (change.EntryId is null)
            {
                var candidates = Entries
                    .Where(e => (change.CommittedAt - e.OccurredAt).Duration() <= window)
                    .OrderBy(e => e.Lsn)
                    .ToList();
                // Only the row of the change's own rank: a change whose row has not arrived yet stays pending (it must not take the
                // row of an earlier change of the same transaction).
                if (rank < candidates.Count && !ordered.Any(c => c.EntryId == candidates[rank].EntryId))
                {
                    var entry = candidates[rank];
                    change = change with { EntryId = entry.EntryId };
                    paired.Add((change.ChangeId, entry.EntryId));
                }
            }

            changes.Add(change);
        }

        return (this with { Changes = changes }, paired);
    }

    private DateTimeOffset Later(DateTimeOffset at) => at > LastSeenAt ? at : LastSeenAt;
}
