using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProcureAi.Assistant.Application.Ai;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>An error answer of the stub model server: the status, the body and extra headers (e.g. <c>retry-after-ms</c>).</summary>
public sealed record StubModelError(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>An OpenAI-style error body.</summary>
    public static string ErrorBody(string message, string code) =>
        JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error", code } });
}

/// <summary>
/// A real Kestrel server playing an OpenAI-compatible model endpoint (Azure Foundry's <c>/openai/v1/</c>): <c>POST chat/completions</c>
/// (streamed or not) and <c>POST embeddings</c>, so the Assistant's real OpenAI SDK client, retries and error handling are what a test
/// exercises. The answer calls (<c>#task: answer</c>) can be scripted: errors queued for the next calls (<see cref="EnqueueAnswerError"/>),
/// or every call failing (<see cref="FailAnswers"/>, with the request's prompt available to echo); otherwise they stream
/// <see cref="AnswerText"/> and report <see cref="PromptTokens"/> and <see cref="CompletionTokens"/>. Every other call succeeds.
/// </summary>
public sealed class StubModelServer : IAsyncDisposable
{
    public const string AnswerText = "Purchases above the threshold need finance approval [S1].";
    public const int PromptTokens = 40;
    public const int CompletionTokens = 20;

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<StubModelError> _answerErrors = new();
    private Func<string, StubModelError>? _failAll;
    private int _answerRequests;

    private StubModelServer(WebApplication app) => _app = app;

    /// <summary>The base URL to configure as <c>AI:Endpoint</c>.</summary>
    public string Endpoint { get; private set; } = "";

    /// <summary>How many answer calls reached the server (retries included).</summary>
    public int AnswerRequests => Volatile.Read(ref _answerRequests);

    public void EnqueueAnswerError(StubModelError error) => _answerErrors.Enqueue(error);

    /// <summary>Every answer call fails with what <paramref name="error"/> makes of the request body; null answers again.</summary>
    public void FailAnswers(Func<string, StubModelError>? error) => Volatile.Write(ref _failAll, error);

    /// <summary>The settings that point the Assistant at this server through the Azure Foundry provider (deployment names as models).</summary>
    public Dictionary<string, string?> Settings => new()
    {
        ["AI:Provider"] = nameof(AiProvider.AzureFoundry),
        ["AI:Endpoint"] = Endpoint,
        ["AI:ApiKey"] = "stub-model-key",
        ["AI:Chat:Model"] = "chat",
        ["AI:Embedding:Model"] = "embeddings",
        // Only the answer calls reach the scripted part: no classifier and no chunk context calls.
        ["Assistant:Guard:Classifier"] = "None",
        ["Assistant:Ingestion:ContextualRetrieval"] = "false",
    };

    public static async Task<StubModelServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new StubModelServer(app);

        app.MapPost("/openai/v1/chat/completions", async (HttpContext http) =>
        {
            var body = await new StreamReader(http.Request.Body, Encoding.UTF8).ReadToEndAsync(http.RequestAborted);
            using var json = JsonDocument.Parse(body);
            var streamed = json.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
            var answer = body.Contains(AiTasks.Line(AiTasks.Answer), StringComparison.Ordinal);
            if (answer)
            {
                Interlocked.Increment(ref stub._answerRequests);
                var error = stub._answerErrors.TryDequeue(out var queued) ? queued : Volatile.Read(ref stub._failAll)?.Invoke(body);
                if (error is not null)
                {
                    await WriteErrorAsync(http, error);
                    return;
                }
            }

            var text = answer ? AnswerText : "ok";
            if (streamed)
            {
                await WriteStreamAsync(http, text);
            }
            else
            {
                await WriteJsonAsync(http, new
                {
                    id = "chatcmpl-stub",
                    @object = "chat.completion",
                    created = 0,
                    model = "chat",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
                    usage = Usage(),
                });
            }
        });

        app.MapPost("/openai/v1/embeddings", async (HttpContext http) =>
        {
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var inputs = json.RootElement.GetProperty("input");
            var count = inputs.ValueKind == JsonValueKind.Array ? inputs.GetArrayLength() : 1;
            var base64 = json.RootElement.TryGetProperty("encoding_format", out var format) && format.GetString() == "base64";
            var vector = new float[1536];
            vector[0] = 1;
            object embedding = base64 ? Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan())) : vector;
            await WriteJsonAsync(http, new
            {
                @object = "list",
                model = "embeddings",
                data = Enumerable.Range(0, count).Select(i => new { @object = "embedding", index = i, embedding }).ToArray(),
                usage = new { prompt_tokens = count, total_tokens = count },
            });
        });

        await app.StartAsync();
        stub.Endpoint = app.Urls.First().TrimEnd('/') + "/openai/v1/";
        return stub;
    }

    private static object Usage() => new { prompt_tokens = PromptTokens, completion_tokens = CompletionTokens, total_tokens = PromptTokens + CompletionTokens };

    private static async Task WriteErrorAsync(HttpContext http, StubModelError error)
    {
        http.Response.StatusCode = error.Status;
        foreach (var (name, value) in error.Headers ?? new Dictionary<string, string>())
        {
            http.Response.Headers[name] = value;
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(error.Body, http.RequestAborted);
    }

    private static async Task WriteJsonAsync(HttpContext http, object value)
    {
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(value), http.RequestAborted);
    }

    private static async Task WriteStreamAsync(HttpContext http, string text)
    {
        http.Response.ContentType = "text/event-stream";
        async Task Send(object chunk)
        {
            await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(chunk) + "\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
        }

        object Chunk(object[] choices, object? usage = null) =>
            new { id = "chatcmpl-stub", @object = "chat.completion.chunk", created = 0, model = "chat", choices, usage };

        await Send(Chunk([new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null }]));
        await Send(Chunk([new { index = 0, delta = new { }, finish_reason = "stop" }]));
        await Send(Chunk([], Usage()));
        await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
