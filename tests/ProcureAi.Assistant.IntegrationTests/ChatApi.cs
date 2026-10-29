using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ProcureAi.BuildingBlocks.Web.Auth;
using Shouldly;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>One server-sent event: its name and its JSON data.</summary>
public sealed record SseFrame(string Event, JsonElement Data)
{
    /// <summary>The <c>text</c> of a <c>delta</c> or <c>replace</c> event; empty for the others.</summary>
    public string Text => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
}

/// <summary>The answer stream of one question: the status, the frames and (for an error before the stream) the problem body.</summary>
public sealed record AskResult(HttpStatusCode Status, string? ContentType, IReadOnlyList<SseFrame> Frames, string Body)
{
    public IEnumerable<SseFrame> Of(string name) => Frames.Where(f => f.Event == name);

    public string StreamedText => string.Concat(Of("delta").Select(f => f.Text));

    public SseFrame Done => Frames.Single(f => f.Event == "done");
}

/// <summary>Calls of the conversations API (plan 05 Task 5a) and a reader of its event stream.</summary>
internal static class ChatApi
{
    public const string Conversations = "/api/v1/assistant/conversations";
    public const string Quota = "/api/v1/assistant/quota";
    public const string AdminConversations = "/api/v1/assistant/admin/conversations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string User(string tenant, string user = "rita") => AssistantApp.Token(tenant, "requester", user);

    public static string Admin(string tenant) => AssistantApp.Token(tenant, "admin", "ada");

    /// <summary>A ready policy document of <paramref name="tenant"/> with one section per text (the section's parent text is the text itself).</summary>
    public static Task<SeededDocument> SeedPolicyAsync(AssistantApp app, string tenant, params string[] texts) =>
        SearchSeed.AddDocumentAsync(
            app, tenant, "Procurement policy", 1,
            [.. texts.Select((text, i) => (1, new SeedSection(["Procurement policy", $"{i + 1} Rule"], text, text)))]);

    public static HttpRequestMessage Request(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public static async Task<Guid> CreateAsync(HttpClient client, string token)
    {
        using var request = Request(HttpMethod.Post, Conversations, token, new { });
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return json.GetProperty("id").GetGuid();
    }

    public static HttpRequestMessage AskRequest(string token, Guid conversation, string text, string mode = "new") =>
        Request(HttpMethod.Post, $"{Conversations}/{conversation}/messages", token, new { text, mode });

    /// <summary>Asks and reads the whole stream (or the error before it).</summary>
    public static async Task<AskResult> AskAsync(HttpClient client, string token, Guid conversation, string text, string mode = "new")
    {
        using var request = AskRequest(token, conversation, text, mode);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (response.StatusCode != HttpStatusCode.OK || contentType != "text/event-stream")
        {
            return new AskResult(response.StatusCode, contentType, [], await response.Content.ReadAsStringAsync(Ct));
        }

        var frames = new List<SseFrame>();
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        await foreach (var frame in ReadFramesAsync(stream, Ct))
        {
            frames.Add(frame);
        }

        return new AskResult(response.StatusCode, contentType, frames, "");
    }

    /// <summary>Asks a question that must be answered to the end without an error.</summary>
    public static async Task<AskResult> AskOkAsync(HttpClient client, string token, Guid conversation, string text, string mode = "new")
    {
        var result = await AskAsync(client, token, conversation, text, mode);
        result.Status.ShouldBe(HttpStatusCode.OK, result.Body);
        result.Of("error").ShouldBeEmpty();
        result.Frames[^1].Event.ShouldBe("done");
        return result;
    }

    /// <summary>
    /// Asks once the conversation is free again: after a client left, the server saves what was streamed and then releases the lock, a
    /// moment after the saved message can already be read. A 409 is retried for up to 5 s.
    /// </summary>
    public static async Task<AskResult> AskOkOnceFreeAsync(HttpClient client, string token, Guid conversation, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var result = await AskAsync(client, token, conversation, text);
            if (result.Status != HttpStatusCode.Conflict || DateTime.UtcNow > deadline)
            {
                result.Status.ShouldBe(HttpStatusCode.OK, result.Body);
                result.Of("error").ShouldBeEmpty();
                result.Frames[^1].Event.ShouldBe("done");
                return result;
            }

            await Task.Delay(100, Ct);
        }
    }

    /// <summary>The events of a <c>text/event-stream</c> body as they arrive (<c>event:</c> and <c>data:</c> lines, a blank line ends one).</summary>
    public static async IAsyncEnumerable<SseFrame> ReadFramesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseFrame(name ?? "message", JsonDocument.Parse(data.ToString()).RootElement.Clone());
                }

                name = null;
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string token)
    {
        using var request = Request(method, url, token);
        return await client.SendAsync(request, Ct);
    }

    public static async Task<JsonElement> GetOkAsync(HttpClient client, string url, string token)
    {
        using var response = await SendAsync(client, HttpMethod.Get, url, token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public static Task<JsonElement> ConversationAsync(HttpClient client, string token, Guid id) =>
        GetOkAsync(client, $"{Conversations}/{id}", token);

    public static JsonElement[] Messages(JsonElement conversation) => [.. conversation.GetProperty("messages").EnumerateArray()];

    /// <summary>Waits until the conversation's last message matches (an answer saved after the stream ended, e.g. on a client abort).</summary>
    public static async Task<JsonElement> WaitForLastMessageAsync(
        HttpClient client, string token, Guid id, Func<JsonElement, bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var messages = Messages(await ConversationAsync(client, token, id));
            if (messages.Length > 0 && condition(messages[^1]))
            {
                return messages[^1];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new ShouldAssertException($"The last message never matched; it is {(messages.Length > 0 ? messages[^1].ToString() : "none")}.");
            }

            await Task.Delay(200, Ct);
        }
    }

    /// <summary>A request with the admin API key for <paramref name="tenant"/>, on behalf of <paramref name="onBehalfOf"/> when given.</summary>
    public static HttpRequestMessage AdminKeyRequest(HttpMethod method, string url, string tenant, string? onBehalfOf = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ApiKeyDefaults.KeyHeader, TestAuth.ApiKey);
        request.Headers.Add(ApiKeyDefaults.TenantHeader, tenant);
        if (onBehalfOf is not null)
        {
            request.Headers.Add(ApiKeyDefaults.OnBehalfOfUserHeader, onBehalfOf);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
