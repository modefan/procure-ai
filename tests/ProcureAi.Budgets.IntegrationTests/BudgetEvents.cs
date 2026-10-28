using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ProcureAi.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ProcureAi.Budgets.IntegrationTests;

/// <summary>What kind of outcome event arrived on the <c>budgets</c> exchange (told apart by the shape of the JSON body).</summary>
public enum BudgetEventKind
{
    Reserved,
    Failed,
    Released,
    Spent,
}

public sealed record BudgetEvent(BudgetEventKind Kind, Guid RequestId, string TenantId, JsonElement Body)
{
    public string? Reason => Kind == BudgetEventKind.Failed ? Body.GetProperty("reason").GetString() : null;
}

/// <summary>A private queue bound to the <c>budgets</c> exchange that keeps every event, so tests can wait for several at once.</summary>
public sealed class BudgetEvents : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly ConcurrentQueue<BudgetEvent> _events = new();

    private BudgetEvents(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public IReadOnlyList<BudgetEvent> All => [.. _events];

    /// <summary>Binds before returning, so nothing published afterwards is missed. The exchange exists once the API has started.</summary>
    public static async Task<BudgetEvents> StartAsync(string rabbitConnectionString)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(rabbitConnectionString) }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true)).QueueName;
        await channel.QueueBindAsync(queue, Exchanges.Budgets, routingKey: "");
        var events = new BudgetEvents(connection, channel);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            var body = JsonDocument.Parse(Encoding.UTF8.GetString(delivery.Body.Span)).RootElement.Clone();
            var kind = body.TryGetProperty("reason"u8, out JsonElement _) ? BudgetEventKind.Failed
                : body.TryGetProperty("fiscalYear"u8, out JsonElement _) ? BudgetEventKind.Reserved
                : body.TryGetProperty("paymentId"u8, out JsonElement _) ? BudgetEventKind.Spent
                : BudgetEventKind.Released;
            events._events.Enqueue(new BudgetEvent(
                kind, body.GetProperty("requestId").GetGuid(), body.GetProperty("tenantId").GetString()!, body));
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(queue, autoAck: true, consumer);
        return events;
    }

    public IReadOnlyList<BudgetEvent> Of(Guid requestId) => [.. _events.Where(e => e.RequestId == requestId)];

    /// <summary>Waits until <paramref name="done"/> holds for the events of the request; fails with what did arrive.</summary>
    public async Task<IReadOnlyList<BudgetEvent>> WaitForAsync(
        Guid requestId, Func<IReadOnlyList<BudgetEvent>, bool> done, TimeSpan timeout)
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
            $"The expected events for {requestId} did not arrive within {timeout}. Seen: {string.Join(", ", Of(requestId).Select(e => e.Kind))}");
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
