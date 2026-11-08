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

    /// <summary>Azure OpenAI's prompt content filter answer (as Azure sent it on 2026-11-07), with the prompt echoed into the message.</summary>
    public static StubModelError ContentFilter(string echo) => new(
        400,
        JsonSerializer.Serialize(new
        {
            error = new
            {
                message = "The response was filtered due to the prompt triggering Azure OpenAI's content management policy. " + echo,
                type = (string?)null,
                param = "prompt",
                code = "content_filter",
                status = 400,
                innererror = new
                {
                    code = "ResponsibleAIPolicyViolation",
                    content_filter_result = new { jailbreak = new { detected = true, filtered = true } },
                },
            },
        }));
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
    private volatile StubAnswerMode _answerMode;
    private readonly TaskCompletionSource _answerStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _answerRequests;
    private int _chatRequests;
    private int _embeddingRequests;

    private StubModelServer(WebApplication app) => _app = app;

    /// <summary>The base URL to configure as <c>AI:Endpoint</c>.</summary>
    public string Endpoint { get; private set; } = "";

    /// <summary>How many answer calls reached the server (retries included).</summary>
    public int AnswerRequests => Volatile.Read(ref _answerRequests);

    /// <summary>How many chat calls of any task (answer, classify, scan, context ...) reached the server; each reports
    /// <see cref="PromptTokens"/> + <see cref="CompletionTokens"/>.</summary>
    public int ChatRequests => Volatile.Read(ref _chatRequests);

    /// <summary>How many embedding calls reached the server; each reports one token per input.</summary>
    public int EmbeddingRequests => Volatile.Read(ref _embeddingRequests);

    /// <summary>A scan's reply: a valid invoice for the scan task's structured output.</summary>
    public const string ScanJson =
        """{"supplierName":"Stub Supplier (Pty) Ltd","supplierVatNumber":"4123456789","invoiceNumber":"INV-1","invoiceDate":"2026-10-20","orderNumber":null,"lines":[{"description":"Desk","quantity":1,"unitPriceExclVat":100,"lineTotalExclVat":100}],"vat":15,"totalExclVat":100,"totalInclVat":115}""";

    /// <summary>
    /// How streamed answers end: normally, stalled after their first text until the caller hangs up (<see cref="AnswerStalled"/> completes
    /// once one is), or cut by the content filter (<c>finish_reason: content_filter</c>, then the usage, as Azure ends a filtered completion).
    /// </summary>
    public void AnswerMode(StubAnswerMode mode) => _answerMode = mode;

    /// <summary>Completes when a stalled answer has sent its first text (<see cref="StubAnswerMode.Stall"/>).</summary>
    public Task AnswerStalled => _answerStalled.Task;

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
        // No classifier and no chunk context calls unless a test asks for them (each answers too: SAFE, "ok").
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
            Interlocked.Increment(ref stub._chatRequests);
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

            var text = answer ? AnswerText
                : body.Contains(AiTasks.Line(AiTasks.Classify), StringComparison.Ordinal) ? "SAFE"
                : body.Contains(AiTasks.Line(AiTasks.Scan), StringComparison.Ordinal) ? ScanJson
                : "ok";
            if (streamed)
            {
                await WriteStreamAsync(http, text, answer ? stub._answerMode : StubAnswerMode.Normal, stub._answerStalled);
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
            Interlocked.Increment(ref stub._embeddingRequests);
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

    private static async Task WriteStreamAsync(HttpContext http, string text, StubAnswerMode mode, TaskCompletionSource stalled)
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
        if (mode == StubAnswerMode.Stall)
        {
            // The first text is out; the rest (and the usage) never comes before the caller hangs up.
            stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, http.RequestAborted);
        }

        await Send(Chunk([new { index = 0, delta = new { }, finish_reason = mode == StubAnswerMode.Filter ? "content_filter" : "stop" }]));
        await Send(Chunk([], Usage()));
        await http.Response.WriteAsync("data: [DONE]\n\n", http.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>How the stub's streamed answers end (<see cref="StubModelServer.AnswerMode"/>).</summary>
public enum StubAnswerMode
{
    Normal,
    Stall,
    Filter,
}
