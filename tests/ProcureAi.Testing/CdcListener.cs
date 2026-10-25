using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace ProcureAi.Testing;

/// <summary>A message Debezium published to the <c>cdc</c> exchange.</summary>
public sealed record CdcMessage(string RoutingKey, JsonElement Envelope)
{
    public string Operation => Envelope.GetProperty("op").GetString()!;

    public long TransactionId => Envelope.GetProperty("source").GetProperty("txId").GetInt64();

    public JsonElement After => Envelope.GetProperty("after");

    public JsonElement Before => Envelope.GetProperty("before");
}

/// <summary>A private queue bound to the <c>cdc</c> exchange, so a test sees every change made after it was created.</summary>
public sealed class CdcListener : IAsyncDisposable
{
    public const string Exchange = "cdc";

    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly string _queue;

    private CdcListener(IConnection connection, IChannel channel, string queue)
    {
        _connection = connection;
        _channel = channel;
        _queue = queue;
    }

    public static async Task<CdcListener> StartAsync(string rabbitConnectionString)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(rabbitConnectionString) }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true)).QueueName;
        // Everything the exchange carries, whatever the routing key.
        await channel.QueueBindAsync(queue, Exchange, routingKey: "#");
        return new CdcListener(connection, channel, queue);
    }

    /// <summary>Waits for the first message that matches; all other messages seen meanwhile are kept in <see cref="Seen"/>.</summary>
    public async Task<CdcMessage> WaitForAsync(Func<CdcMessage, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await _channel.BasicGetAsync(_queue, autoAck: true);
            if (result is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                continue;
            }

            var message = new CdcMessage(
                result.RoutingKey,
                JsonDocument.Parse(Encoding.UTF8.GetString(result.Body.Span)).RootElement.Clone());
            Seen.Add(message);
            if (match(message))
            {
                return message;
            }
        }

        throw new TimeoutException($"No matching CDC message arrived within {timeout}. Seen: {string.Join(", ", Seen.Select(m => m.RoutingKey))}");
    }

    public List<CdcMessage> Seen { get; } = [];

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
