using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ProcureAi.Simulators.Webhooks;

/// <summary>Whose receiver gets the webhook.</summary>
public enum WebhookTarget
{
    Purchasing,
    Invoicing,
}

/// <summary>A webhook waiting for its time. The body is serialized once, so every attempt sends (and signs) the same bytes.</summary>
/// <remarks><c>Generation</c>: the queue's generation when it was scheduled; a reset starts a new one and drops everything older.</remarks>
public sealed record ScheduledWebhook(string SupplierOrderId, WebhookTarget Target, string Type, DateTimeOffset DueAt, byte[] Body, int Generation = 0);

/// <summary>The hand-over from the supplier (which decides to send a webhook at some time) to <see cref="WebhookSender"/> (which waits and delivers).</summary>
public sealed class WebhookQueue
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Channel<ScheduledWebhook> _channel = Channel.CreateUnbounded<ScheduledWebhook>(
        new UnboundedChannelOptions { SingleReader = true });

    private int _generation;

    public ChannelReader<ScheduledWebhook> Reader => _channel.Reader;

    /// <summary>The current generation: a webhook of an older one was scheduled before the last reset and is never sent.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public void Schedule<T>(string supplierOrderId, WebhookTarget target, string type, DateTimeOffset dueAt, T payload) =>
        _channel.Writer.TryWrite(new ScheduledWebhook(
            supplierOrderId, target, type, dueAt, JsonSerializer.SerializeToUtf8Bytes(payload, Json), Generation));

    /// <summary>
    /// The nightly demo reset: every webhook scheduled so far (waiting for its time or between retries) is dropped. Order numbers start again
    /// after a reset, so an old webhook would otherwise reach the new order with the same number.
    /// </summary>
    public void Reset() => Interlocked.Increment(ref _generation);

    /// <summary>True when <paramref name="webhook"/> was scheduled before the last <see cref="Reset"/>.</summary>
    public bool IsStale(ScheduledWebhook webhook) => webhook.Generation != Generation;
}
