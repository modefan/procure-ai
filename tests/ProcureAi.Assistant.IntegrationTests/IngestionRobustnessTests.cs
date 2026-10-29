using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using ProcureAi.Assistant.Infrastructure.Persistence;
using Shouldly;
using Wolverine;
using static ProcureAi.Assistant.IntegrationTests.PolicyDocumentsApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// Review fixes of plan 05 Task 2b: unexpected failures end the version, the guard reads titles and headings too, progress is stored
/// once per step, the upload limit is enforced before the body is read, and the version races (stale jobs, removal, quick replaces,
/// reuse per tenant).
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class IngestionRobustnessTests(AssistantApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Marker(string name) => $"{name}{Guid.NewGuid():N}"[..(name.Length + 12)];

    private static bool Ended(PolicyDocumentDto d) => d.Status is "Ready" or "Flagged" or "Failed";

    [Fact]
    public async Task Unexpected_failure_fails_the_version_without_dead_letter()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);
        var marker = Marker("bug");
        app.Embeddings.ThrowWhenContains(marker, () => new InvalidOperationException("A bug in the pipeline."));

        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown(marker, 2));
        var ended = await WaitForAsync(app.Client, token, document.Id, Ended);

        ended.Status.ShouldBe("Failed");
        ended.FailureReason.ShouldBe("Ingestion failed unexpectedly.");
        app.Embeddings.CallsWith(marker).ShouldBe(1);
        (await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id", ("id", document.Id))).ShouldBe(0L);
        (await DeadLettersAsync(document.Id)).ShouldBe(0L);
    }

    [Fact]
    public async Task Injection_in_a_heading_is_flagged()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);
        var marker = Marker("heading");
        var content = System.Text.Encoding.UTF8.GetBytes(
            $"# Policy {marker}\n\n## 1 Rules {MarkerPromptShield.Marker}\n\nPurchases of {marker} need approval.\n");

        var document = await UploadOkAsync(app.Client, token, "policy.md", content);
        var ended = await WaitForAsync(app.Client, token, document.Id, Ended);

        ended.Status.ShouldBe("Flagged");
        ended.FlaggedReason.ShouldBe(MarkerPromptShield.Reason);
        (await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id", ("id", document.Id))).ShouldBe(0L);
    }

    [Fact]
    public async Task Injection_in_the_title_is_flagged()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);
        var marker = Marker("title");

        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown(marker, 2), $"Policy {MarkerPromptShield.Marker}");
        var ended = await WaitForAsync(app.Client, token, document.Id, Ended);

        ended.Status.ShouldBe("Flagged");
        app.Embeddings.TextsWith(marker).ShouldBeEmpty();
    }

    [Fact]
    public async Task Progress_is_stored_once_per_10_percent_step()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);

        // 80 children in 20 batches of 4: twice as many batches as steps.
        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown(Marker("steps"), 80));
        var ready = await WaitForStatusAsync(app.Client, token, document.Id, "Ready");

        ready.Progress.ShouldBe(100);
        // Every write of the row is one audit row: Checking, Embedding, the 10 steps and Ready, never one per batch.
        var updates = await app.ScalarAsync<long>(
            "SELECT count(*) FROM audit_entries WHERE table_name = 'policy_documents' AND primary_key = @id AND operation = 'Update'",
            ("id", document.Id.ToString()));
        updates.ShouldBeLessThanOrEqualTo(13L);
    }

    [Fact]
    public async Task Upload_over_30_MB_is_413_before_the_body_is_read()
    {
        var tenant = AssistantApp.NewTenant();
        var content = new byte[31 * 1024 * 1024];
        "# Big"u8.CopyTo(content);
        Array.Fill(content, (byte)'a', 5, content.Length - 5);

        using var response = await UploadAsync(app.Client, Admin(tenant), "big.md", content);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, await response.Content.ReadAsStringAsync(Ct));
        (await app.ScalarAsync<long>("SELECT count(*) FROM policy_documents WHERE tenant_id = @tenant", ("tenant", tenant))).ShouldBe(0L);
    }

    [Fact]
    public async Task Stale_v1_message_after_v2_is_ready_only_clears_v1()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);
        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown(Marker("stale1"), 3));
        await WaitForStatusAsync(app.Client, token, document.Id, "Ready");
        await ReplaceOkAsync(app.Client, token, document.Id, "policy.md", Markdown(Marker("stale2"), 3));
        var v2 = await WaitForStatusAsync(app.Client, token, document.Id, "Ready", version: 2);

        // A leftover chunk of version 1 (as a crashed v1 job could leave) and a late redelivery of the v1 message.
        await using (var db = RawDb())
        {
            var leftover = new Chunk(Guid.CreateVersion7(), tenant, SourceType.Policy, ChunkKind.Child, null, 99, "x", "leftover v1", "h",
                DateTimeOffset.UtcNow, document.Id, 1);
            leftover.SetEmbedding(AiOptions.FakeEmbeddingModel, FakeEmbeddingGenerator.Embed("leftover"), null);
            db.Chunks.Add(leftover);
            await db.SaveChangesAsync(Ct);
        }

        await SendAsync(new IngestPolicyDocument(document.Id, 1));
        await WaitUntilAsync(async () =>
            await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id AND document_version = 1", ("id", document.Id)) == 0);

        var after = await GetOkAsync(app.Client, token, document.Id);
        after.ShouldBe(v2);
        (await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id AND document_version = 2 AND kind = 'Child'", ("id", document.Id)))
            .ShouldBe(3L);
    }

    [Fact]
    public async Task Remove_during_ingestion_stops_and_clears_the_version()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);
        var marker = Marker("removing");
        var late = Marker("late");
        var hold = app.Embeddings.HoldWhenContains(late);
        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown(marker, 8, i => i == 8 ? $"Rule 8 of {marker} {late}." : null!));
        await hold.Entered.WaitAsync(Ingestion, Ct);

        try
        {
            var during = await GetOkAsync(app.Client, token, document.Id);
            using var removed = await RemoveAsync(app.Client, token, document.Id, during.RowVersion);
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        finally
        {
            hold.Release();
        }

        // The job's next commit sees the removal and deletes what it stored (the first batch and the parents).
        await WaitUntilAsync(async () =>
            await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id", ("id", document.Id)) == 0);
        (await app.ScalarAsync<string>("SELECT status FROM policy_documents WHERE id = @id", ("id", document.Id))).ShouldNotBe("Ready");
        (await app.ScalarAsync<int>("SELECT current_version FROM policy_documents WHERE id = @id", ("id", document.Id))).ShouldBe(0);
    }

    [Fact]
    public async Task Two_quick_replaces_end_with_the_last_version()
    {
        var tenant = AssistantApp.NewTenant();
        var token = Admin(tenant);
        var last = Marker("v3");
        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown(Marker("v1"), 4));
        await WaitForStatusAsync(app.Client, token, document.Id, "Ready");

        await ReplaceOkAsync(app.Client, token, document.Id, "policy.md", Markdown(Marker("v2"), 4));
        // Version 2's job moves the row on (Checking, Embedding), so the client re-reads the ETag on 412, as the UI does.
        for (var attempt = 1; ; attempt++)
        {
            var current = await GetOkAsync(app.Client, token, document.Id);
            using var v3 = await ReplaceAsync(app.Client, token, document.Id, current.RowVersion, "policy.md", Markdown(last, 4));
            if (v3.StatusCode == HttpStatusCode.PreconditionFailed && attempt < 10)
            {
                continue;
            }

            v3.StatusCode.ShouldBe(HttpStatusCode.Accepted, await v3.Content.ReadAsStringAsync(Ct));
            break;
        }

        var ready = await WaitForStatusAsync(app.Client, token, document.Id, "Ready", version: 3);
        ready.CurrentVersion.ShouldBe(3);
        var texts = await SearchableTextsAsync(app, tenant, document.Id);
        texts.Count.ShouldBe(4);
        texts.ShouldAllBe(t => t.Contains(last));
        await WaitUntilAsync(async () =>
            await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id AND document_version <> 3", ("id", document.Id)) == 0);
    }

    [Fact]
    public async Task Reuse_does_not_cross_tenants()
    {
        var marker = Marker("shared");
        var content = Markdown(marker, 3);
        var first = AssistantApp.NewTenant();
        var second = AssistantApp.NewTenant();

        var a = await UploadOkAsync(app.Client, Admin(first), "policy.md", content);
        await WaitForStatusAsync(app.Client, Admin(first), a.Id, "Ready");
        var b = await UploadOkAsync(app.Client, Admin(second), "policy.md", content);
        await WaitForStatusAsync(app.Client, Admin(second), b.Id, "Ready");

        // The same text in another tenant is embedded again: nothing of one tenant is copied into another.
        app.Embeddings.TextsWith(marker).Count.ShouldBe(6);
    }

    private AssistantDbContext RawDb() =>
        new(new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql(app.Infra.DbConnectionString, npgsql => npgsql.UseVector())
            .Options);

    private async Task SendAsync(object message)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().SendAsync(message);
    }

    private Task<long> DeadLettersAsync(Guid documentId) =>
        app.ScalarAsync<long>(
            "SELECT count(*) FROM wolverine.wolverine_dead_letters WHERE message_type LIKE '%IngestPolicyDocument%' AND position(convert_to(@id, 'UTF8') in body) > 0",
            ("id", documentId.ToString()));

    private static async Task WaitUntilAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow + Ingestion;
        while (DateTime.UtcNow < deadline)
        {
            if (await done())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        throw new TimeoutException("The expected state was not reached.");
    }
}
