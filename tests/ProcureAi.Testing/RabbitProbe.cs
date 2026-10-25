using RabbitMQ.Client;

namespace ProcureAi.Testing;

/// <summary>Binds a throwaway queue to an exchange and reads what arrives, headers included.</summary>
public sealed class RabbitProbe(string connectionString)
{
    public async Task<string> BindQueueAsync(string exchange)
    {
        var queue = $"probe-{Guid.NewGuid():N}";
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: true);
        await channel.QueueBindAsync(queue, exchange, routingKey: "");
        return queue;
    }

    public async Task<uint> CountAsync(string queue)
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    /// <summary>Waits until at least <paramref name="count"/> messages are queued.</summary>
    public async Task WaitForCountAsync(string queue, uint count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (await CountAsync(queue) < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Fewer than {count} messages arrived on '{queue}' within {timeout}.");
            }

            await Task.Delay(200);
        }
    }

    /// <summary>The headers of the next message, as text.</summary>
    public async Task<Dictionary<string, string>> TakeHeadersAsync(string queue)
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var message = await channel.BasicGetAsync(queue, autoAck: true)
            ?? throw new InvalidOperationException($"'{queue}' is empty.");
        return (message.BasicProperties.Headers ?? new Dictionary<string, object?>())
            .ToDictionary(h => h.Key, h => h.Value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : h.Value?.ToString() ?? "");
    }

    private ConnectionFactory Factory() => new() { Uri = new Uri(connectionString) };

    /// <summary>
    /// Waits for the first message (as JSON) that matches, discarding the others. Messages from earlier work (the outbox delivers
    /// after the commit) may arrive first, so a test must name what it waits for: tenant, kind, user.
    /// </summary>
    public async Task<System.Text.Json.JsonElement> WaitForMessageAsync(
        string queue, Func<System.Text.Json.JsonElement, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var seen = new List<string>();
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        while (DateTime.UtcNow < deadline)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(result.Body.Span);
            var body = System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
            if (match(body))
            {
                return body;
            }

            seen.Add(text);
        }

        throw new TimeoutException($"No matching message arrived on '{queue}' within {timeout}. Seen: {string.Join(" | ", seen)}");
    }
}
