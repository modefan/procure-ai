using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.BuildingBlocks.Application;
using Shouldly;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>Calls of the policy documents API as an admin of a tenant (the stub Access gives the <c>admin</c> role <c>admin.policies</c>).</summary>
internal static class PolicyDocumentsApi
{
    public const string Url = "/api/v1/assistant/policy-documents";
    public const string AdminUser = "ada";
    public const string AdminName = "Ada Admin";

    /// <summary>How long ingestion of a small document may take on the Fake provider (scheduled retries poll every few seconds).</summary>
    public static readonly TimeSpan Ingestion = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Admin(string tenant) => AssistantApp.Token(tenant, "admin", AdminUser);

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token, string fileName, byte[] content, string? title = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Uploads and returns the accepted document (fails the test on anything but 202).</summary>
    public static async Task<PolicyDocumentDto> UploadOkAsync(HttpClient client, string token, string fileName, byte[] content, string? title = null)
    {
        using var response = await UploadAsync(client, token, fileName, content, title);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PolicyDocumentDto>(Ct))!;
    }

    public static async Task<HttpResponseMessage> ReplaceAsync(HttpClient client, string token, Guid id, uint rowVersion, string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(content), "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Url}/{id}/file") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        return await client.SendAsync(request, Ct);
    }

    /// <summary>Replaces the file at the document's current row version (read just before) and returns the accepted document.</summary>
    public static async Task<PolicyDocumentDto> ReplaceOkAsync(HttpClient client, string token, Guid id, string fileName, byte[] content)
    {
        var current = await GetOkAsync(client, token, id);
        using var response = await ReplaceAsync(client, token, id, current.RowVersion, fileName, content);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PolicyDocumentDto>(Ct))!;
    }

    public static async Task<HttpResponseMessage> RemoveAsync(HttpClient client, string token, Guid id, uint rowVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Url}/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        return await client.SendAsync(request, Ct);
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string token, Guid id) =>
        client.SendAsync(AssistantApp.Get($"{Url}/{id}", token), Ct);

    public static async Task<PolicyDocumentDto> GetOkAsync(HttpClient client, string token, Guid id)
    {
        using var response = await GetAsync(client, token, id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PolicyDocumentDto>(Ct))!;
    }

    /// <summary>Polls the document until <paramref name="done"/> holds; fails with the last state seen.</summary>
    public static async Task<PolicyDocumentDto> WaitForAsync(
        HttpClient client, string token, Guid id, Func<PolicyDocumentDto, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Ingestion);
        PolicyDocumentDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetOkAsync(client, token, id);
            if (done(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }

        throw new TimeoutException($"Policy document {id} did not reach the expected state. Last seen: {last}");
    }

    public static Task<PolicyDocumentDto> WaitForStatusAsync(HttpClient client, string token, Guid id, string status, int? version = null) =>
        WaitForAsync(client, token, id, d => d.Status == status && (version is null || d.Version == version));

    /// <summary>A Markdown policy with one short section per rule (each section is one parent and one child chunk).</summary>
    public static byte[] Markdown(string marker, int sections, Func<int, string>? text = null)
    {
        var builder = new StringBuilder($"# Test policy {marker}\n\n");
        for (var i = 1; i <= sections; i++)
        {
            builder.Append($"## 1.{i} Rule {i}\n\n");
            builder.Append(text?.Invoke(i) ?? $"Rule {i} of {marker}: purchases of kind {i} need a written reason and the approval of a manager.");
            builder.Append("\n\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>A context of the tenant as a caller sees it (the tenant filters apply).</summary>
    public static AssistantDbContext TenantDb(AssistantApp app, string tenant) =>
        new(new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql(app.Infra.DbConnectionString, npgsql => npgsql.UseVector())
            .Options, new TestCaller(tenant));

    /// <summary>The texts of the document's child chunks that retrieval may return now (<see cref="SearchableChunks"/>).</summary>
    public static async Task<List<string>> SearchableTextsAsync(AssistantApp app, string tenant, Guid documentId)
    {
        await using var db = TenantDb(app, tenant);
        return await SearchableChunks.Of(db).AsNoTracking()
            .Where(c => c.DocumentId == documentId && c.Kind == ChunkKind.Child)
            .OrderBy(c => c.Ordinal).Select(c => c.Text).ToListAsync(Ct);
    }

    private sealed class TestCaller(string tenant) : ICallerContext
    {
        public string UserId => AdminUser;

        public string ActorId => AdminUser;

        public string? TenantId => tenant;

        public string? CallingService => null;

        public CallerKind Kind => CallerKind.User;
    }
}
