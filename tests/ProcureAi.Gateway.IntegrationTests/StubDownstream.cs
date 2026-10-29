using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>What the stub downstream service saw.</summary>
public sealed record SeenRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A real Kestrel server standing in for the services and the web app; records every request it gets. A WebSocket request is accepted,
/// told the path it reached, and closed.
/// </summary>
public sealed class StubDownstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SeenRequest> _seen = new();

    private StubDownstream(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public IReadOnlyList<SeenRequest> Seen => [.. _seen];

    public SeenRequest Last => _seen.Last();

    /// <summary>The pause between the events of the stub's answer stream (the Fake model's delay in the assistant test).</summary>
    public static readonly TimeSpan StreamDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>How many <c>delta</c> events the stub's answer stream sends before <c>done</c>.</summary>
    public const int StreamDeltas = 5;

    /// <summary>When the stub finished writing its last answer stream (<see cref="System.Diagnostics.Stopwatch"/> ticks); 0 before.</summary>
    public long StreamFinishedAt => Interlocked.Read(ref _streamFinishedAt);

    private long _streamFinishedAt;

    /// <summary>
    /// The Assistant's answer stream as the service writes it: server-sent events, each flushed on its own, <see cref="StreamDelay"/>
    /// apart. A gateway that buffered the response would deliver them all at once, after the last.
    /// </summary>
    private async Task StreamAnswerAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        for (var i = 0; i < StreamDeltas; i++)
        {
            await context.Response.WriteAsync($"event: delta\ndata: {{\"text\":\"word{i} \"}}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(StreamDelay, context.RequestAborted);
        }

        await context.Response.WriteAsync("event: done\ndata: {}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        Interlocked.Exchange(ref _streamFinishedAt, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public static async Task<StubDownstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        StubDownstream? stub = null;
        app.Run(async context =>
        {
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            stub!._seen.Enqueue(new SeenRequest(context.Request.Method, context.Request.Path.Value!, headers));
            if (context.WebSockets.IsWebSocketRequest)
            {
                // A WebSocket (the live hub): say which path it reached, then close.
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var hello = System.Text.Encoding.UTF8.GetBytes($"connected:{context.Request.Path.Value}");
                await socket.SendAsync(hello, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/v1/assistant")
                && context.Request.Path.Value!.EndsWith("/messages", StringComparison.Ordinal))
            {
                await stub!.StreamAnswerAsync(context);
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { stub = true, host = context.Request.Host.Value, path = context.Request.Path.Value });
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        stub = new StubDownstream(app, url);
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
