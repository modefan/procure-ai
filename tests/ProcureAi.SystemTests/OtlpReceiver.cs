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
/// (e.g. Polly's <c>resilience.polly.strategy.events</c> with <c>event.name=OnCircuitOpened</c>). The service name and the metric name
/// are found as plain UTF-8 strings in the (uncompressed) export; the attributes must sit together on one data point, which a minimal
/// protobuf walk checks (<see cref="OtlpWire"/>). A watch names the service whose exports it judges; since OTLP counters are cumulative,
/// it remembers whether that service's latest export matched, so a test can re-arm it before provoking the event and know whether the
/// event had already happened. Nothing else is kept: trace and log exports are discarded unread.
/// </summary>
public sealed class OtlpReceiver : IAsyncDisposable
{
    private const string MetricsService = "opentelemetry.proto.collector.metrics.v1.MetricsService";

    private const int GrpcFrameHeader = 5;

    // gRPC frame of an empty message (the Export*ServiceResponse): not compressed, length 0.
    private static readonly byte[] EmptyGrpcMessage = [0, 0, 0, 0, 0];

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Starts watching the metric exports of <paramref name="service"/> (its <c>service.name</c>) for a data point of
    /// <paramref name="metric"/> that carries every one of <paramref name="attributes"/> (all on the same data point).
    /// </summary>
    public void WatchMetrics(string name, string service, string metric, params (string Key, string Value)[] attributes) =>
        _watches[name] = new Watch(Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(metric), attributes);

    /// <summary>
    /// Forgets when the watch matched, so <see cref="FirstSeen"/> only reports matches from now on. Returns whether the service's latest
    /// export already matched (the counter is cumulative: then the event happened before, and later exports will match regardless).
    /// </summary>
    public bool Rearm(string name)
    {
        var watch = _watches[name];
        lock (watch)
        {
            watch.FirstSeen = null;
            return watch.LatestMatched;
        }
    }

    /// <summary>When a metric export of the service first matched the watch (since it was set or re-armed), or null.</summary>
    public DateTimeOffset? FirstSeen(string name)
    {
        var watch = _watches[name];
        lock (watch)
        {
            return watch.FirstSeen;
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Path.Value?.Contains(MetricsService, StringComparison.Ordinal) == true)
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            Interlocked.Increment(ref _metricExports);
            // The body is one gRPC frame: a compressed flag (0 here: no grpc-encoding), a 4-byte length, then the protobuf message.
            var frame = body.GetBuffer().AsSpan(0, (int)body.Length);
            if (frame.Length >= GrpcFrameHeader && frame[0] == 0)
            {
                Judge(frame[GrpcFrameHeader..]);
            }
        }
        else
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/grpc";
        await context.Response.Body.WriteAsync(EmptyGrpcMessage, context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }

    private void Judge(ReadOnlySpan<byte> export)
    {
        foreach (var watch in _watches.Values)
        {
            if (export.IndexOf(watch.Service) < 0)
            {
                continue;
            }

            var matched = export.IndexOf(watch.Metric) >= 0 && OtlpWire.HasDataPointWith(export, watch.Attributes);
            lock (watch)
            {
                watch.LatestMatched = matched;
                if (matched && watch.FirstSeen is null)
                {
                    watch.FirstSeen = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    private sealed class Watch(byte[] service, byte[] metric, (string Key, string Value)[] attributes)
    {
        public byte[] Service { get; } = service;

        public byte[] Metric { get; } = metric;

        public (string Key, string Value)[] Attributes { get; } = attributes;

        public bool LatestMatched { get; set; }

        public DateTimeOffset? FirstSeen { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Just enough protobuf to find a data point by its attributes, without the OTLP types: walks every length-delimited field as a nested
/// message (a field that does not parse as one is skipped) and collects the <c>KeyValue</c> children of each message
/// (<c>1: key</c>, <c>2: AnyValue { 1: string_value }</c>), the way a data point holds its attributes.
/// </summary>
internal static class OtlpWire
{
    private const int MaxDepth = 12;

    public static bool HasDataPointWith(ReadOnlySpan<byte> message, (string Key, string Value)[] attributes) =>
        Search(message, attributes, 0);

    private static bool Search(ReadOnlySpan<byte> message, (string Key, string Value)[] attributes, int depth)
    {
        var found = 0;
        var position = 0;
        while (position < message.Length)
        {
            if (!TryReadField(message, ref position, out var wireType, out var value, out _))
            {
                return false;
            }

            if (wireType != 2)
            {
                continue;
            }

            if (TryKeyValue(value, out var key, out var text))
            {
                for (var i = 0; i < attributes.Length; i++)
                {
                    if (attributes[i].Key == key && attributes[i].Value == text)
                    {
                        found |= 1 << i;
                    }
                }
            }
            else if (depth < MaxDepth && Search(value, attributes, depth + 1))
            {
                return true;
            }
        }

        return found == (1 << attributes.Length) - 1;
    }

    private static bool TryKeyValue(ReadOnlySpan<byte> message, out string key, out string value)
    {
        key = value = "";
        var position = 0;
        var fields = 0;
        while (position < message.Length)
        {
            if (!TryReadField(message, ref position, out var wireType, out var field, out var number) || wireType != 2)
            {
                return false;
            }

            if (number == 1)
            {
                key = Encoding.UTF8.GetString(field);
                fields |= 1;
            }
            else if (number == 2)
            {
                var inner = 0;
                if (!TryReadField(field, ref inner, out var innerType, out var stringValue, out var innerNumber)
                    || innerType != 2 || innerNumber != 1 || inner != field.Length)
                {
                    return false;
                }

                value = Encoding.UTF8.GetString(stringValue);
                fields |= 2;
            }
            else
            {
                return false;
            }
        }

        return fields == 3;
    }

    private static bool TryReadField(ReadOnlySpan<byte> message, ref int position, out int wireType, out ReadOnlySpan<byte> value, out ulong number)
    {
        value = default;
        number = 0;
        wireType = -1;
        if (!TryReadVarint(message, ref position, out var tag) || tag >> 3 == 0)
        {
            return false;
        }

        number = tag >> 3;
        wireType = (int)(tag & 7);
        switch (wireType)
        {
            case 0:
                return TryReadVarint(message, ref position, out _);
            case 1:
                position += 8;
                return position <= message.Length;
            case 5:
                position += 4;
                return position <= message.Length;
            case 2:
                if (!TryReadVarint(message, ref position, out var length) || length > (ulong)(message.Length - position))
                {
                    return false;
                }

                value = message.Slice(position, (int)length);
                position += (int)length;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> message, ref int position, out ulong result)
    {
        result = 0;
        for (var shift = 0; shift < 64 && position < message.Length; shift += 7)
        {
            var b = message[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
