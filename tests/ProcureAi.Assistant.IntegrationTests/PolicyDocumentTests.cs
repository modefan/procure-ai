using System.Net;
using System.Net.Http.Json;
using System.Text;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.BuildingBlocks.Application;
using Shouldly;
using static ProcureAi.Assistant.IntegrationTests.PolicyDocumentsApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>
/// The policy documents API (plan 05 Task 2b, ruling 12): uploads of real PDF and Markdown files become ready with their chunks; the size
/// limit, the content check and the permission; tenant isolation; soft removal.
/// </summary>
[Collection(AssistantCollection.Name)]
public sealed class PolicyDocumentTests(AssistantApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewAdminTenant()
    {
        var tenant = AssistantApp.NewTenant();
        app.Access.SetDisplayName(tenant, AdminUser, AdminName);
        return tenant;
    }

    [Fact]
    public async Task Upload_markdown_becomes_ready_with_chunks()
    {
        var tenant = NewAdminTenant();
        var token = Admin(tenant);

        var accepted = await UploadOkAsync(app.Client, token, "policy.md", await File.ReadAllBytesAsync("Files/policy.md", Ct), "Acme procurement policy");

        accepted.Title.ShouldBe("Acme procurement policy");
        accepted.FileName.ShouldBe("policy.md");
        accepted.Version.ShouldBe(1);
        accepted.CurrentVersion.ShouldBe(0);
        accepted.Status.ShouldBe("Uploaded");
        accepted.UploadedByName.ShouldBe(AdminName);
        var ready = await WaitForStatusAsync(app.Client, token, accepted.Id, "Ready");
        ready.CurrentVersion.ShouldBe(1);
        ready.Progress.ShouldBe(100);
        ready.ChunkCount.ShouldBeGreaterThan(3);
        ready.FlaggedReason.ShouldBeNull();
        ready.FailureReason.ShouldBeNull();

        // Children are embedded with the model's name and a context; parents carry the section text only.
        var id = accepted.Id;
        (await app.ScalarAsync<long>(
                "SELECT count(*) FROM chunks WHERE document_id = @id AND document_version = 1 AND kind = 'Child' AND embedding IS NOT NULL " +
                "AND embedding_model = @model AND context IS NOT NULL AND tenant_id = @tenant AND parent_id IS NOT NULL",
                ("id", id), ("model", AiOptions.FakeEmbeddingModel), ("tenant", tenant)))
            .ShouldBe(ready.ChunkCount);
        (await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id AND kind = 'Child'", ("id", id))).ShouldBe(ready.ChunkCount);
        (await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id AND kind = 'Parent' AND embedding IS NULL", ("id", id)))
            .ShouldBeGreaterThan(0);
        (await app.ScalarAsync<string>(
                "SELECT section_path FROM chunks WHERE document_id = @id AND kind = 'Child' AND text LIKE '%R 20 000%' ORDER BY ordinal LIMIT 1", ("id", id)))
            .ShouldNotBeNull().ShouldContain("4.2 Approval thresholds");
        (await SearchableTextsAsync(app, tenant, id)).Count.ShouldBe(ready.ChunkCount);
        // The file is kept as uploaded, with its detected type.
        (await app.ScalarAsync<string>("SELECT content_type FROM policy_document_files WHERE document_id = @id AND version = 1", ("id", id)))
            .ShouldBe("text/markdown");
    }

    [Fact]
    public async Task Upload_pdf_becomes_ready()
    {
        var tenant = NewAdminTenant();
        var token = Admin(tenant);

        var accepted = await UploadOkAsync(app.Client, token, "policy.pdf", await File.ReadAllBytesAsync("Files/policy.pdf", Ct));

        accepted.Title.ShouldBe("policy");
        var ready = await WaitForStatusAsync(app.Client, token, accepted.Id, "Ready");
        ready.ChunkCount.ShouldBeGreaterThan(3);
        (await app.ScalarAsync<string>("SELECT content_type FROM policy_document_files WHERE document_id = @id", ("id", accepted.Id)))
            .ShouldBe("application/pdf");
        (await SearchableTextsAsync(app, tenant, accepted.Id)).ShouldContain(t => t.Contains("R 20 000"));
    }

    [Fact]
    public async Task Upload_over_10_MB_is_413()
    {
        var tenant = NewAdminTenant();
        var content = Encoding.UTF8.GetBytes(new string('a', 10 * 1024 * 1024 + 1));

        using var tooLarge = await UploadAsync(app.Client, Admin(tenant), "big.md", content);
        using var atLimit = await UploadAsync(app.Client, Admin(tenant), "limit.md", content[..(10 * 1024 * 1024)]);

        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, await tooLarge.Content.ReadAsStringAsync(Ct));
        tooLarge.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        atLimit.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await app.ScalarAsync<long>("SELECT count(*) FROM policy_documents WHERE tenant_id = @tenant", ("tenant", tenant))).ShouldBe(1L);
    }

    [Fact]
    public async Task Upload_exe_renamed_pdf_is_415()
    {
        var tenant = NewAdminTenant();
        var token = Admin(tenant);
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00];

        using var renamedPdf = await UploadAsync(app.Client, token, "policy.pdf", executable);
        using var renamedMarkdown = await UploadAsync(app.Client, token, "policy.md", executable);
        using var exe = await UploadAsync(app.Client, token, "setup.exe", executable);
        using var pdfNamedText = await UploadAsync(app.Client, token, "policy.txt", await File.ReadAllBytesAsync("Files/policy.pdf", Ct));
        using var invalidUtf8 = await UploadAsync(app.Client, token, "policy.md", [0x23, 0x20, 0xC3, 0x28, 0x41]);

        foreach (var response in new[] { renamedPdf, renamedMarkdown, exe, pdfNamedText, invalidUtf8 })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType, await response.Content.ReadAsStringAsync(Ct));
        }

        (await app.ScalarAsync<long>("SELECT count(*) FROM policy_documents WHERE tenant_id = @tenant", ("tenant", tenant))).ShouldBe(0L);
    }

    [Fact]
    public async Task Upload_without_admin_policies_is_403()
    {
        var tenant = AssistantApp.NewTenant();
        var content = Markdown("no-permission", 1);

        using var requester = await UploadAsync(app.Client, AssistantApp.Token(tenant, "requester"), "policy.md", content);
        using var finance = await UploadAsync(app.Client, AssistantApp.Token(tenant, "finance"), "policy.md", content);
        using var list = await app.Client.SendAsync(AssistantApp.Get(Url, AssistantApp.Token(tenant, "requester")), Ct);

        requester.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        finance.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.ScalarAsync<long>("SELECT count(*) FROM policy_documents WHERE tenant_id = @tenant", ("tenant", tenant))).ShouldBe(0L);
    }

    [Fact]
    public async Task Other_tenant_document_is_404()
    {
        var owner = NewAdminTenant();
        var other = NewAdminTenant();
        var document = await UploadOkAsync(app.Client, Admin(owner), "policy.md", Markdown("other-tenant", 2));
        var mine = await GetOkAsync(app.Client, Admin(owner), document.Id);

        using var get = await GetAsync(app.Client, Admin(other), document.Id);
        using var replace = await ReplaceAsync(app.Client, Admin(other), document.Id, mine.RowVersion, "policy.md", Markdown("other-tenant-2", 2));
        using var remove = await RemoveAsync(app.Client, Admin(other), document.Id, mine.RowVersion);
        using var list = await app.Client.SendAsync(AssistantApp.Get(Url, Admin(other)), Ct);

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        replace.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        remove.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await list.Content.ReadAsStringAsync(Ct)).ShouldNotContain(document.Id.ToString());
        (await GetOkAsync(app.Client, Admin(owner), document.Id)).Version.ShouldBe(1);
    }

    [Fact]
    public async Task Remove_hides_chunks_from_search()
    {
        var tenant = NewAdminTenant();
        var token = Admin(tenant);
        var document = await UploadOkAsync(app.Client, token, "policy.md", Markdown("remove-me", 3));
        var ready = await WaitForStatusAsync(app.Client, token, document.Id, "Ready");
        (await SearchableTextsAsync(app, tenant, document.Id)).Count.ShouldBe(3);

        using var stale = await RemoveAsync(app.Client, token, document.Id, ready.RowVersion + 1);
        using var removed = await RemoveAsync(app.Client, token, document.Id, ready.RowVersion);

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync(Ct));
        (await SearchableTextsAsync(app, tenant, document.Id)).ShouldBeEmpty();
        using var get = await GetAsync(app.Client, token, document.Id);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var list = await app.Client.SendAsync(AssistantApp.Get(Url, token), Ct);
        (await list.Content.ReadAsStringAsync(Ct)).ShouldNotContain(document.Id.ToString());
        // A soft delete: the row and its chunks stay.
        (await app.ScalarAsync<bool>("SELECT removed FROM policy_documents WHERE id = @id", ("id", document.Id))).ShouldBeTrue();
        (await app.ScalarAsync<long>("SELECT count(*) FROM chunks WHERE document_id = @id AND kind = 'Child'", ("id", document.Id))).ShouldBe(3L);
    }

    [Fact]
    public async Task List_shows_the_tenants_documents_with_a_body_etag()
    {
        var tenant = NewAdminTenant();
        var token = Admin(tenant);
        await UploadOkAsync(app.Client, token, "b.md", Markdown("list-b", 1), "B policy");
        await UploadOkAsync(app.Client, token, "a.md", Markdown("list-a", 1), "A policy");

        using var response = await app.Client.SendAsync(AssistantApp.Get(Url, token), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull().IsWeak.ShouldBeTrue();
        var page = await response.Content.ReadFromJsonAsync<PagedResult<PolicyDocumentDto>>(Ct);
        page!.Items.Select(d => d.Title).ShouldBe(["A policy", "B policy"]);
        page.Total.ShouldBe(2);
        page.Items.ShouldAllBe(d => d.UploadedByName == AdminName);
    }
}
