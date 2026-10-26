using System.Net;
using System.Net.Sockets;
using System.Text;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace ProcureAi.Testing;

/// <summary>
/// A RabbitMQ container of its own (fixed host port) that a test can stop and restart, for broker-outage tests, with a helper queue
/// to observe delivery. Capped like the shared broker (<see cref="RabbitFixture"/>).
/// </summary>
public sealed class RabbitContainer : IAsyncDisposable
{
    private readonly RabbitMqContainer _container;

    private RabbitContainer(RabbitMqContainer container) => _container = container;

    public string ConnectionString => _container.GetConnectionString();

    public static async Task<RabbitContainer> StartAsync()
    {
        var container = new RabbitMqBuilder("rabbitmq:4.3-management")
            // A fixed host port, so the same connection string works again after a restart.
            .WithPortBinding(FreePort(), 5672)
            .WithCreateParameterModifier(p => TestLimits.Apply(p, "RabbitMq"))
            .Build();
        await container.StartAsync();
        return new RabbitContainer(container);
    }

    public Task StopAsync() => _container.StopAsync();

    public Task ResumeAsync() => _container.StartAsync();

    public async Task BindQueueAsync(string queue, string exchange)
    {
        await using var connection = await Factory().CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, exchange, routingKey: "");
    }

    public async Task<string> WaitForMessageAsync(string queue, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = await Factory().CreateConnectionAsync();
                await using var channel = await connection.CreateChannelAsync();
                var result = await channel.BasicGetAsync(queue, autoAck: true);
                if (result is not null)
                {
                    return Encoding.UTF8.GetString(result.Body.Span);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                // The broker is still starting.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"No message arrived on '{queue}' within {timeout}.");
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private ConnectionFactory Factory() => new() { Uri = new Uri(ConnectionString) };
}
