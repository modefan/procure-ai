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
public sealed record ScheduledWebhook(string SupplierOrderId, WebhookTarget Target, string Type, DateTimeOffset DueAt, byte[] Body);

/// <summary>The hand-over from the supplier (which decides to send a webhook at some time) to <see cref="WebhookSender"/> (which waits and delivers).</summary>
public sealed class WebhookQueue
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Channel<ScheduledWebhook> _channel = Channel.CreateUnbounded<ScheduledWebhook>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ScheduledWebhook> Reader => _channel.Reader;

    public void Schedule<T>(string supplierOrderId, WebhookTarget target, string type, DateTimeOffset dueAt, T payload) =>
        _channel.Writer.TryWrite(new ScheduledWebhook(supplierOrderId, target, type, dueAt, JsonSerializer.SerializeToUtf8Bytes(payload, Json)));
}
