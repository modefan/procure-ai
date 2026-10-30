using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Retrieval.PastPurchases;
using ProcureAi.BuildingBlocks.Domain;
using Wolverine;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.PolicyDocumentsApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Past purchases (plan 05 Task 3, ruling 9): the Assistant keeps a copy of each request from <c>RequestSubmitted</c>,
/// <c>RequestApproved</c> and <c>RequestCompleted</c> (queue <c>assistant-api-requests</c>), in any order, and indexes a completed one as
/// a purchase chunk. A completion without its submit data is retried (1 s apart in the tests, three times), then skipped.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class PastPurchasesTests(AssistantApp app)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Marker(string name) => $"{name}{Guid.NewGuid():N}"[..(name.Length + 10)];

    private static RequestSubmitted Submitted(string tenant, string title, Guid? id = null) => new(
        id ?? Guid.NewGuid(), tenant, "PR-0982", title, "tom", "IT-200", 140_000m, DateTimeOffset.Parse("2026-09-01T08:00:00Z"), "SUP-7",
        [new RequestLineItem(1, "LAP-14", "Laptop 14 inch", 2, 50_000m), new RequestLineItem(2, "DOCK-1", "Docking station", 1, 21_739.13m)]);

    private static RequestApproved Approved(RequestSubmitted submitted) => new(
        submitted.RequestId, submitted.TenantId, submitted.Number, submitted.TotalInclVat, ["maria", "sipho"],
        DateTimeOffset.Parse("2026-09-02T08:00:00Z"), "tom");

    // 23:30 UTC on the 13th is 01:30 on the 14th in South Africa: the chunk says the 14th.
    private static RequestCompleted Completed(RequestSubmitted submitted) => new(
        submitted.RequestId, submitted.TenantId, submitted.Number, submitted.TotalInclVat, DateTimeOffset.Parse("2026-09-13T23:30:00Z"), "tom");

    private static string ExpectedText(string title) =>
        $"PR-0982 · {title} · IT-200 · R 140 000,00 incl. VAT · approved by Maria Naidoo, Sipho Dlamini · completed 2026-09-14"
        + " · lines: 2 x Laptop 14 inch, 1 x Docking station";

    private void NameApprovers(string tenant)
    {
        app.Access.SetDisplayName(tenant, "maria", "Maria Naidoo");
        app.Access.SetDisplayName(tenant, "sipho", "Sipho Dlamini");
    }

    [Fact]
    public async Task Completed_request_becomes_purchase_chunk_with_approver_names()
    {
        var tenant = AssistantApp.NewTenant();
        NameApprovers(tenant);
        var title = $"Laptops for IT {Marker("laptops")}";
        var submitted = Submitted(tenant, title);

        // All three at once: they may be handled in any order, and concurrently.
        await app.PublishAsync(submitted, Exchanges.Requests, tenant);
        await app.PublishAsync(Approved(submitted), Exchanges.Approvals, tenant);
        await app.PublishAsync(Completed(submitted), Exchanges.Approvals, tenant);

        var chunk = await WaitForChunkAsync(submitted.RequestId, c => c.Text.Contains("approved by"));
        chunk.Text.ShouldBe(ExpectedText(title));
        chunk.TenantId.ShouldBe(tenant);
        chunk.SourceType.ShouldBe(SourceType.Purchase);
        chunk.Kind.ShouldBe(ChunkKind.Child);
        chunk.DocumentId.ShouldBeNull();
        chunk.Context.ShouldBeNull("purchase chunks are embedded without a context step");
        chunk.Embedding.ShouldNotBeNull().Length.ShouldBe(Chunk.EmbeddingDimensions);
        (await PurchaseAsync(submitted.RequestId)).ShouldNotBeNull().Status.ShouldBe(PurchaseStatus.Completed);

        // Found by search as a purchase, with its number as the reference.
        await using var db = TenantDb(app, tenant);
        var hits = await SearchSeed.Search(app, db).SearchAsync(
            new SearchRequest(title, new HashSet<SourceType> { SourceType.Purchase }, 6), Ct);
        var hit = hits.ShouldHaveSingleItem();
        hit.ChunkId.ShouldBe(chunk.Id);
        hit.Type.ShouldBe(SourceType.Purchase);
        hit.Reference.ShouldBe("PR-0982");
        hit.Title.ShouldBe(title);
        hit.SectionPath.ShouldBeEmpty();
        hit.ParentText.ShouldBe(chunk.Text);
    }

    [Fact]
    public async Task Completed_before_submitted_still_indexed_after_retry()
    {
        var tenant = AssistantApp.NewTenant();
        NameApprovers(tenant);
        var title = $"Desks {Marker("desks")}";
        var submitted = Submitted(tenant, title);
        var deadLetters = await app.DeadLetterCountAsync();

        await app.PublishAsync(Approved(submitted), Exchanges.Approvals, tenant);
        await WaitForPurchaseAsync(submitted.RequestId, p => p.ApprovedAt is not null);
        await app.PublishAsync(Completed(submitted), Exchanges.Approvals, tenant);
        // The first attempt failed (no submit data yet) and a retry is scheduled; nothing is stored for the completion meanwhile.
        await WaitForScheduledRetryAsync();
        (await PurchaseAsync(submitted.RequestId)).ShouldNotBeNull().CompletedAt.ShouldBeNull();
        await app.PublishAsync(submitted, Exchanges.Requests, tenant);

        var chunk = await WaitForChunkAsync(submitted.RequestId);
        chunk.Text.ShouldBe(ExpectedText(title));
        (await app.DeadLetterCountAsync()).ShouldBe(deadLetters);
    }

    [Fact]
    public async Task Completed_without_submit_data_is_skipped_after_retries_then_indexed_on_submit()
    {
        var tenant = AssistantApp.NewTenant();
        NameApprovers(tenant);
        var title = $"Chairs {Marker("chairs")}";
        var submitted = Submitted(tenant, title);
        var deadLetters = await app.DeadLetterCountAsync();

        await app.PublishAsync(Completed(submitted), Exchanges.Approvals, tenant);

        // After the third retry the completion is kept on a placeholder row and the message ends: logged, not dead-lettered.
        var placeholder = await WaitForPurchaseAsync(submitted.RequestId, p => p.CompletedAt is not null);
        placeholder.HasSubmitData.ShouldBeFalse();
        (await ChunkCountAsync(submitted.RequestId)).ShouldBe(0L);
        (await app.DeadLetterCountAsync()).ShouldBe(deadLetters);

        // When the submit data does come (and the approval after it), the purchase is indexed.
        await app.PublishAsync(submitted, Exchanges.Requests, tenant);
        await app.PublishAsync(Approved(submitted), Exchanges.Approvals, tenant);
        var chunk = await WaitForChunkAsync(submitted.RequestId, c => c.Text.Contains("approved by"));
        chunk.Text.ShouldBe(ExpectedText(title));
        (await ChunkCountAsync(submitted.RequestId)).ShouldBe(1L);
    }

    [Fact]
    public async Task Rejected_request_is_not_indexed()
    {
        var tenant = AssistantApp.NewTenant();
        var title = $"Gaming chairs {Marker("rejected")}";
        var submitted = Submitted(tenant, title);
        var deadLetters = await app.DeadLetterCountAsync();

        await app.PublishAsync(submitted, Exchanges.Requests, tenant);
        // Events the Assistant has no handler for arrive on the same queue and are discarded.
        await app.PublishAsync(
            new RequestRejected(submitted.RequestId, tenant, submitted.Number, "Not in budget", "maria", DateTimeOffset.UtcNow, "tom"),
            Exchanges.Approvals, tenant);
        await app.PublishAsync(new RequestCancelled(submitted.RequestId, tenant, submitted.Number, "tom", "Changed my mind", DateTimeOffset.UtcNow), Exchanges.Requests, tenant);

        await WaitForPurchaseAsync(submitted.RequestId, _ => true);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        (await PurchaseAsync(submitted.RequestId)).ShouldNotBeNull().Status.ShouldBe(PurchaseStatus.Submitted);
        (await ChunkCountAsync(submitted.RequestId)).ShouldBe(0L);
        app.Embeddings.TextsWith(title).ShouldBeEmpty();
        (await app.DeadLetterCountAsync()).ShouldBe(deadLetters);
    }

    [Fact]
    public async Task Duplicate_completed_message_indexes_once()
    {
        var tenant = AssistantApp.NewTenant();
        NameApprovers(tenant);
        var title = $"Monitors {Marker("monitors")}";
        var submitted = Submitted(tenant, title);
        await app.PublishAsync(submitted, Exchanges.Requests, tenant);
        await app.PublishAsync(Approved(submitted), Exchanges.Approvals, tenant);
        await WaitForPurchaseAsync(submitted.RequestId, p => p.HasSubmitData && p.ApprovedAt is not null);

        var probe = app.Probe;
        var captured = await probe.BindQueueAsync(Exchanges.Approvals);
        await app.PublishAsync(Completed(submitted), Exchanges.Approvals, tenant);
        var raw = await probe.WaitForRawAsync(captured, Timeout);
        await WaitForChunkAsync(submitted.RequestId);

        // The same message (same message ID) delivered again: the durable inbox drops it, so it is not embedded a second time.
        await probe.RepublishAsync(Exchanges.Approvals, raw);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        app.Embeddings.TextsWith(title).Count.ShouldBe(1);
        (await ChunkCountAsync(submitted.RequestId)).ShouldBe(1L);
    }

    [Fact]
    public async Task Two_events_of_a_new_request_at_once_make_one_row_with_both()
    {
        var tenant = AssistantApp.NewTenant();
        var deadLetters = await app.DeadLetterCountAsync();
        var requests = Enumerable.Range(1, 12).Select(i => Submitted(tenant, $"Parallel {i} {Marker("parallel")}")).ToList();

        // For each new request, the submit and the approval at the same moment: both handlers find no row and insert one, and the
        // loser's unique violation (23505 on PK_purchases) is retried as a lost race, then finds the winner's row and adds its part.
        await Task.WhenAll(requests.SelectMany(r => new[]
        {
            app.PublishAsync(r, Exchanges.Requests, tenant),
            app.PublishAsync(Approved(r), Exchanges.Approvals, tenant),
        }));

        foreach (var request in requests)
        {
            var purchase = await WaitForPurchaseAsync(request.RequestId, p => p.HasSubmitData && p.ApprovedAt is not null);
            purchase.Title.ShouldBe(request.Title);
            purchase.ApproverIdList.ShouldBe(["maria", "sipho"]);
        }

        var ids = requests.Select(r => r.RequestId).ToArray();
        (await app.ScalarAsync<long>("SELECT count(*) FROM purchases WHERE request_id = ANY(@ids)", ("ids", ids))).ShouldBe(requests.Count);
        (await app.DeadLetterCountAsync()).ShouldBe(deadLetters);
    }

    private async Task<Chunk> WaitForChunkAsync(Guid requestId, Func<Chunk, bool>? until = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            await using var db = NewDb();
            var chunk = await db.Chunks.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(c => c.PurchaseRequestId == requestId, Ct);
            if (chunk is not null && (until is null || until(chunk)))
            {
                return chunk;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No purchase chunk for {requestId} within {Timeout} (last: {chunk?.Text ?? "none"}).");
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<Purchase> WaitForPurchaseAsync(Guid requestId, Func<Purchase, bool> until)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (await PurchaseAsync(requestId) is { } purchase && until(purchase))
            {
                return purchase;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The purchase {requestId} did not get there within {Timeout}.");
            }

            await Task.Delay(200, Ct);
        }
    }

    [Fact]
    public async Task Placeholder_older_than_max_age_is_deleted_and_newer_kept()
    {
        var tenant = AssistantApp.NewTenant();
        var maxAge = app.Services.GetRequiredService<IOptions<AssistantOptions>>().Value.Purchases.PlaceholderMaxAge;
        var now = DateTimeOffset.UtcNow;
        var stale = Purchase.Placeholder(Guid.NewGuid(), tenant, "PR-0001", now - maxAge - TimeSpan.FromHours(1));
        var recent = Purchase.Placeholder(Guid.NewGuid(), tenant, "PR-0002", now - maxAge + TimeSpan.FromHours(1));
        // Old, but its submit data arrived: a real purchase, never deleted.
        var submittedOld = new Purchase(Guid.NewGuid(), tenant, "PR-0003", "Chairs", "IT-200", Money.Zar(1_000m), null, "1 x Chair", now - maxAge - TimeSpan.FromDays(30));
        await using (var db = NewDb())
        {
            db.Purchases.AddRange(stale, recent, submittedOld);
            await db.SaveChangesAsync(Ct);
        }

        await app.Bus.InvokeAsync(new PurgeStalePlaceholders(), Ct);

        (await PurchaseAsync(stale.RequestId)).ShouldBeNull("a placeholder older than Assistant:Purchases:PlaceholderMaxAge is deleted");
        (await PurchaseAsync(recent.RequestId)).ShouldNotBeNull("a newer placeholder may still get its submit data");
        (await PurchaseAsync(submittedOld.RequestId)).ShouldNotBeNull("a purchase with its submit data is not a placeholder");
    }

    /// <summary>Waits until a <c>RequestCompleted</c> waits in the durable inbox for its scheduled retry (the tests of the collection run one at a time).</summary>
    private async Task WaitForScheduledRetryAsync()
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (await app.ScalarAsync<long>(
                   "SELECT count(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type LIKE '%RequestCompleted%'")
               == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No retry of RequestCompleted was scheduled.");
            }

            await Task.Delay(50, Ct);
        }
    }

    private async Task<Purchase?> PurchaseAsync(Guid requestId)
    {
        await using var db = NewDb();
        return await db.Purchases.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.RequestId == requestId, Ct);
    }

    private Task<long> ChunkCountAsync(Guid requestId) =>
        app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE purchase_request_id = @id", ("id", requestId));

    private Infrastructure.Persistence.AssistantDbContext NewDb() =>
        new(new DbContextOptionsBuilder<Infrastructure.Persistence.AssistantDbContext>()
            .UseNpgsql(app.Infra.DbConnectionString, npgsql => npgsql.UseVector()).Options);
}
