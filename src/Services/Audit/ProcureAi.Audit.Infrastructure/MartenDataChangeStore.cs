using JasperFx;
using Marten;
using Marten.Linq;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Audit.Infrastructure;

/// <summary>
/// The data change audit in Marten. Each write runs in a session of its own (not in Wolverine's transaction) and commits the change
/// or interceptor row, its <see cref="AttributionMatch"/> and any attribution it completes together.
/// <para>
/// Concurrency: <see cref="DataChange"/> and <see cref="AttributionMatch"/> use optimistic concurrency (Marten versions). Two
/// arrivals for the same row and transaction at the same moment both read the match, both write it, and one of them fails with a
/// <see cref="ConcurrencyException"/> (or, when both create it, a duplicate insert, rethrown as one); its handler runs again
/// (Wolverine's retry policy) and then finds the other's row, so the pair is matched exactly once and never left pending. The
/// grace timeout and a late match on the same change race the same way through the change's version.
/// </para>
/// </summary>
public sealed class MartenDataChangeStore(IDocumentStore store, TimeProvider clock) : IDataChangeStore
{
    public async Task<Attribution> RecordChangeAsync(DataChange change, long lsn, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();
        if (await session.LoadAsync<DataChange>(change.Id, cancellationToken) is { } stored)
        {
            return stored.Attribution; // redelivery
        }

        var matchId = AttributionMatch.KeyOf(change.Database, change.TransactionId, change.Table, change.PrimaryKey);
        var existing = await session.LoadAsync<AttributionMatch>(matchId, cancellationToken);
        var (match, paired) = (existing ?? AttributionMatch.Start(matchId, change.CommittedAt))
            .WithChange(change.Id, lsn, change.CommittedAt)
            .Pair(window);

        foreach (var (_, entryId) in paired)
        {
            var entry = await session.LoadAsync<InterceptorEntry>(entryId, cancellationToken)
                ?? throw new InvalidOperationException($"Interceptor row {entryId} of match {matchId} is missing.");
            change = change.AttributedTo(entry);
        }

        session.Insert(change);
        Save(session, match, isNew: existing is null);
        await CommitAsync(session, cancellationToken);
        return change.Attribution;
    }

    public async Task RecordInterceptorEntryAsync(InterceptorEntry entry, long lsn, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();
        if (await session.LoadAsync<InterceptorEntry>(entry.Id, cancellationToken) is not null)
        {
            return; // redelivery
        }

        var matchId = AttributionMatch.KeyOf(entry.Database, entry.TransactionId, entry.Table, entry.PrimaryKey);
        var existing = await session.LoadAsync<AttributionMatch>(matchId, cancellationToken);
        var (match, paired) = (existing ?? AttributionMatch.Start(matchId, entry.OccurredAt))
            .WithEntry(entry.Id, lsn, entry.OccurredAt)
            .Pair(window);

        foreach (var (changeId, _) in paired)
        {
            var change = await session.LoadAsync<DataChange>(changeId, cancellationToken)
                ?? throw new InvalidOperationException($"Change {changeId} of match {matchId} is missing.");
            if (change.Attribution.Kind == AttributionKind.OutsideApp
                && await session.LoadAsync<OutsideAppAlert>(changeId, cancellationToken) is { } alert)
            {
                // The row came after the grace period: the change was the app's after all.
                session.Store(alert.Withdrawn(clock.GetUtcNow()));
            }

            session.Update(change.AttributedTo(entry));
        }

        session.Insert(entry);
        Save(session, match, isNew: existing is null);
        await CommitAsync(session, cancellationToken);
    }

    public async Task<DataChange?> ResolveAsync(string changeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession();
        var change = await session.LoadAsync<DataChange>(changeId, cancellationToken);
        if (change is not { IsPending: true })
        {
            return null; // matched meanwhile, decided before, or unknown
        }

        var outside = change.OutsideTheApp();
        session.Update(outside);
        session.Store(OutsideAppAlert.For(outside, now));
        await CommitAsync(session, cancellationToken);
        return outside;
    }

    public async Task<int> PurgeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken) =>
        await PurgeBatchesAsync<DataChange, string>(q => q.Where(c => c.CommittedAt < cutoff).Select(c => c.Id), batchSize, cancellationToken)
        + await PurgeBatchesAsync<InterceptorEntry, Guid>(q => q.Where(e => e.OccurredAt < cutoff).Select(e => e.Id), batchSize, cancellationToken)
        + await PurgeBatchesAsync<AttributionMatch, string>(q => q.Where(m => m.LastSeenAt < cutoff).Select(m => m.Id), batchSize, cancellationToken)
        + await PurgeBatchesAsync<OutsideAppAlert, string>(q => q.Where(a => a.CommittedAt < cutoff).Select(a => a.Id), batchSize, cancellationToken);

    public async Task<PagedResult<DataChange>> ForSubjectAsync(
        string tenantId, Guid subjectId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession();
        var items = await session.Query<DataChange>()
            .Stats(out var stats)
            .Where(c => c.TenantId == tenantId && c.SubjectId == subjectId)
            .OrderBy(c => c.CommittedAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<DataChange>(items, page, pageSize, (int)stats.TotalResults);
    }

    public async Task<PagedResult<DataChange>> SearchAsync(
        DataChangeFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var session = store.QuerySession();
        IQueryable<DataChange> query = session.Query<DataChange>().Stats(out var stats);
        if (filter.TenantId is { } tenant)
        {
            query = query.Where(c => c.TenantId == tenant);
        }

        if (filter.Database is { } database)
        {
            query = query.Where(c => c.Database == database);
        }

        if (filter.Table is { } table)
        {
            query = query.Where(c => c.Table == table);
        }

        if (filter.UserId is { } user)
        {
            query = query.Where(c => c.Attribution.UserId == user);
        }

        if (filter.OutsideOnly)
        {
            query = query.Where(c => c.Attribution.Kind == AttributionKind.OutsideApp);
        }

        if (filter.From is { } from)
        {
            query = query.Where(c => c.CommittedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(c => c.CommittedAt < to);
        }

        var items = await query
            .OrderByDescending(c => c.CommittedAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<DataChange>(items, page, pageSize, (int)stats.TotalResults);
    }

    private static void Save(IDocumentSession session, AttributionMatch match, bool isNew)
    {
        if (isNew)
        {
            session.Insert(match);
        }
        else
        {
            session.Update(match);
        }
    }

    // A duplicate insert of the match (or the change) is the same lost race as a version conflict: the handler runs again.
    private static async Task CommitAsync(IDocumentSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (DocumentAlreadyExistsException exception)
        {
            throw new ConcurrencyException("A concurrent write stored the same audit document first.", exception);
        }
    }

    // Deletes in batches of at most batchSize IDs, each batch in its own short transaction, until none is left.
    private async Task<int> PurgeBatchesAsync<T, TId>(
        Func<IQueryable<T>, IQueryable<TId>> expiredIds, int batchSize, CancellationToken cancellationToken)
        where T : notnull
        where TId : notnull
    {
        var total = 0;
        while (true)
        {
            await using var session = store.LightweightSession();
            var ids = await expiredIds(session.Query<T>()).Take(batchSize).ToListAsync(cancellationToken);
            foreach (var id in ids)
            {
                switch (id)
                {
                    case string text:
                        session.Delete<T>(text);
                        break;
                    case Guid guid:
                        session.Delete<T>(guid);
                        break;
                    default:
                        throw new NotSupportedException($"Document ID type {typeof(TId).Name}.");
                }
            }

            await session.SaveChangesAsync(cancellationToken);
            total += ids.Count;
            if (ids.Count < batchSize)
            {
                return total;
            }
        }
    }
}
