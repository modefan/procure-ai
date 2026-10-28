using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace ProcureAi.SystemTests;

/// <summary>
/// A stand-in for the Aspire dashboard's OTLP endpoint (the testing builder runs no dashboard): an OTLP/gRPC receiver on a free local
/// port that answers every export with success. The AppHost points every service at it, so a test can see a metric a service exported
/// (e.g. Polly's <c>resilience.polly.strategy.events</c> with <c>event.name=OnCircuitOpened</c>). It does not decode protobuf: OTLP
/// carries metric names, attribute keys and values and the <c>service.name</c> as plain UTF-8 strings, so a watch matches when one
/// uncompressed export holds all of its strings. Nothing is kept but the time each watch first matched.
/// </summary>
public sealed class OtlpReceiver : IAsyncDisposable
{
    private const string MetricsService = "opentelemetry.proto.collector.metrics.v1.MetricsService";

    // gRPC frame of an empty message (the Export*ServiceResponse): not compressed, length 0.
    private static readonly byte[] EmptyGrpcMessage = [0, 0, 0, 0, 0];

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte[][]> _watches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);
    private long _metricExports;

    private OtlpReceiver(WebApplication app) => _app = app;

    /// <summary>The address to configure as the OTLP gRPC endpoint (<c>http://127.0.0.1:&lt;port&gt;</c>).</summary>
    public Uri Endpoint { get; private set; } = null!;

    /// <summary>How many metric exports arrived (to tell "nothing exported" from "not exported yet" in a failure message).</summary>
    public long MetricExports => Interlocked.Read(ref _metricExports);

    public static async Task<OtlpReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // gRPC over plain HTTP/2 (prior knowledge), as the OTLP exporters speak it to an http:// endpoint.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        var receiver = new OtlpReceiver(app);
        app.Run(receiver.HandleAsync);
        await app.StartAsync();
        receiver.Endpoint = new Uri(app.Urls.Single());
        return receiver;
    }

    /// <summary>Starts watching metric exports for one that holds every one of <paramref name="texts"/>.</summary>
    public void WatchMetrics(string name, params string[] texts) =>
        _watches[name] = [.. texts.Select(Encoding.UTF8.GetBytes)];

    /// <summary>When a metric export first matched the watch, or null.</summary>
    public DateTimeOffset? FirstSeen(string name) => _firstSeen.TryGetValue(name, out var at) ? at : null;

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        if (context.Request.Path.Value?.Contains(MetricsService, StringComparison.Ordinal) == true)
        {
            Interlocked.Increment(ref _metricExports);
            var bytes = body.GetBuffer().AsSpan(0, (int)body.Length);
            foreach (var (name, texts) in _watches)
            {
                if (!_firstSeen.ContainsKey(name) && ContainsAll(bytes, texts))
                {
                    _firstSeen.TryAdd(name, DateTimeOffset.UtcNow);
                }
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/grpc";
        await context.Response.Body.WriteAsync(EmptyGrpcMessage, context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }

    private static bool ContainsAll(ReadOnlySpan<byte> payload, byte[][] texts)
    {
        foreach (var text in texts)
        {
            if (payload.IndexOf(text) < 0)
            {
                return false;
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
