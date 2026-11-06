using System.Text.Json;
using ProcureAi.Audit.Application;
using ProcureAi.Audit.Application.Abstractions;
using ProcureAi.Audit.Application.Queries;
using ProcureAi.Audit.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Contracts.Requests;
using Shouldly;

namespace ProcureAi.Audit.Tests;

/// <summary>
/// Whose request it is, when Audit holds more than one <c>RequestSubmitted</c> for it (plan 07 carry-over 28): only the first one
/// recorded counts. A second one (forged by someone with broker write access, or a replay with other content) never moves the request
/// into another caller's scope, whatever it says about itself (an earlier <c>OccurredAt</c> included), and in whatever order the store
/// returns them.
/// </summary>
public sealed class RequestReadScopeTests
{
    private const string Tenant = "acme";

    private static readonly DateTimeOffset Submitted = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly RequestScopeOptions Options = new() { Database = "requestsdb", Table = "purchase_requests" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BusinessEvent Submission(Guid id, string requester, string costCenter, DateTimeOffset occurredAt, DateTimeOffset recordedAt) =>
        new(Guid.NewGuid(), Tenant, "RequestSubmitted", id, requester, null, "requests", occurredAt,
            JsonSerializer.Serialize(
                new RequestSubmitted(id, Tenant, "PR-7", "Laptop", requester, costCenter, 30_000m, occurredAt, "SUP-1", []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
        {
            RecordedAt = recordedAt,
        };

    private static (BusinessEvent Real, BusinessEvent Forged) TomsRequestAndAForgery(Guid id) =>
        (Submission(id, "tom", "IT-200", Submitted, Submitted.AddSeconds(1)),
         // Recorded later, but claims to have happened earlier and to be Alice's on her cost center.
         Submission(id, "alice", "CC-100", Submitted.AddHours(-1), Submitted.AddMinutes(10)));

    private static Task ReadAsAsync(Guid id, string user, string costCenter, params BusinessEvent[] stored) =>
        RequestReadScope.EnsureReadableAsync(
            id, Tenant, wholeTenant: false, new FixedScope(new CallerScope(false, user, new HashSet<string> { costCenter })),
            new FixedSubmissions(stored), new NoRows(), Options, "not found", Ct);

    [Fact]
    public async Task Second_request_submitted_event_does_not_change_scope()
    {
        var id = Guid.NewGuid();
        var (real, forged) = TomsRequestAndAForgery(id);

        // The store returns the forgery first: the choice must not depend on the store's order.
        await Should.ThrowAsync<NotFoundException>(() => ReadAsAsync(id, "alice", "CC-100", forged, real));
        await Should.ThrowAsync<NotFoundException>(() => ReadAsAsync(id, "alice", "CC-100", real, forged));
        await ReadAsAsync(id, "mia", "IT-200", forged, real);
        await ReadAsAsync(id, "tom", "NONE", forged, real);
    }

    [Fact]
    public void First_submission_per_request_is_the_first_recorded()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var (real, forged) = TomsRequestAndAForgery(id);
        var otherOne = Submission(other, "ben", "GX-100", Submitted, Submitted);

        var first = RequestReadScope.FirstSubmissions([forged, otherOne, real]);

        first.Count.ShouldBe(2);
        first[id].ShouldBeSameAs(real);
        first[other].ShouldBeSameAs(otherOne);
    }

    private sealed class FixedScope(CallerScope scope) : ICallerScope
    {
        public Task<CallerScope> GetAsync(CancellationToken cancellationToken) => Task.FromResult(scope);
    }

    private sealed class FixedSubmissions(IReadOnlyList<BusinessEvent> stored) : IAuditStore
    {
        public Task<IReadOnlyList<BusinessEvent>> SubmissionsAsync(string tenantId, IReadOnlyCollection<Guid> subjectIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BusinessEvent>>([.. stored.Where(e => e.TenantId == tenantId && subjectIds.Contains(e.SubjectId!.Value))]);

        public Task AppendAsync(BusinessEvent businessEvent, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BusinessEvent>> ForSubjectAsync(string tenantId, Guid subjectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BusinessEvent>> LatestAsync(
            string tenantId, int skip, int limit, IReadOnlyCollection<string> excludedTypes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>No captured requests row: the owner can only come from a submission.</summary>
    private sealed class NoRows : IDataChangeStore
    {
        public Task<DataChange?> LatestRowOfSubjectAsync(string tenantId, string database, string table, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<DataChange?>(null);

        public Task<Attribution> RecordChangeAsync(DataChange change, long lsn, TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordInterceptorEntryAsync(InterceptorEntry entry, long lsn, TimeSpan window, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DataChange?> ResolveAsync(string changeId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RecordDecidedChangeAsync(DataChange change, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DataChange?> LatestChangeAsync(string database, string table, string primaryKey, string exceptId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PagedResult<DataChange>> ForSubjectAsync(string tenantId, Guid subjectId, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PagedResult<DataChange>> SearchAsync(DataChangeFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OpenOutsideAppAlerts> OpenOutsideAppAlertsAsync(int latestPerTenant, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
