using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ProcureAi.Testing;

/// <summary>One message a <see cref="RabbitRecorder"/> received. <c>MessageType</c> is Wolverine's type name, e.g. <c>ProcureAi.Contracts.Approvals.ApprovalRequested</c>.</summary>
public sealed record RecordedMessage(string Source, string MessageType, JsonElement Body)
{
    /// <summary>The short type name (<c>ApprovalRequested</c>).</summary>
    public string Kind => MessageType[(MessageType.LastIndexOf('.') + 1)..];

    public string? Text(string property) =>
        Body.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;

    public Guid? RequestId => Text("requestId") is { } id ? Guid.Parse(id) : null;
}

/// <summary>
/// Keeps every message that reaches some exchanges (each through a private queue bound to it) and some queues (consumed directly:
/// the recorder stands in for the service that owns them), so a test can wait for several at once and assert what did not come.
/// Start it after the service under test, which declares the exchanges and queues.
/// </summary>
public sealed class RabbitRecorder : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly ConcurrentQueue<RecordedMessage> _messages = new();

    private RabbitRecorder(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public IReadOnlyList<RecordedMessage> All => [.. _messages];

    public static async Task<RabbitRecorder> StartAsync(
        string connectionString, IEnumerable<string> exchanges, IEnumerable<string> queues)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(connectionString) }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        var recorder = new RabbitRecorder(connection, channel);
        foreach (var exchange in exchanges)
        {
            var queue = (await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true)).QueueName;
            await channel.QueueBindAsync(queue, exchange, routingKey: "");
            await recorder.ConsumeAsync(queue, exchange);
        }

        foreach (var queue in queues)
        {
            await channel.QueueDeclarePassiveAsync(queue);
            await recorder.ConsumeAsync(queue, queue);
        }

        return recorder;
    }

    private async Task ConsumeAsync(string queue, string source)
    {
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            var body = JsonDocument.Parse(Encoding.UTF8.GetString(delivery.Body.Span)).RootElement.Clone();
            var type = delivery.BasicProperties.Type
                ?? (delivery.BasicProperties.Headers?.TryGetValue("message-type", out var header) == true && header is byte[] bytes
                    ? Encoding.UTF8.GetString(bytes)
                    : "unknown");
            _messages.Enqueue(new RecordedMessage(source, type, body));
            return Task.CompletedTask;
        };
        await _channel.BasicConsumeAsync(queue, autoAck: true, consumer);
    }

    /// <summary>The messages about one request (by their <c>requestId</c>), in arrival order.</summary>
    public IReadOnlyList<RecordedMessage> Of(Guid requestId) => [.. _messages.Where(m => m.RequestId == requestId)];

    /// <summary>Waits until <paramref name="done"/> holds for the messages of the request; fails listing what did arrive.</summary>
    public async Task<IReadOnlyList<RecordedMessage>> WaitForAsync(
        Guid requestId, Func<IReadOnlyList<RecordedMessage>, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var seen = Of(requestId);
            if (done(seen))
            {
                return seen;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"The expected messages for {requestId} did not arrive within {timeout}. Seen: {string.Join(", ", Of(requestId).Select(m => m.Kind))}");
    }

    /// <summary>Waits until <paramref name="kind"/> arrived <paramref name="count"/> times for the request.</summary>
    public Task<IReadOnlyList<RecordedMessage>> WaitForAsync(Guid requestId, string kind, TimeSpan timeout, int count = 1) =>
        WaitForAsync(requestId, seen => seen.Count(m => m.Kind == kind) >= count, timeout);

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
