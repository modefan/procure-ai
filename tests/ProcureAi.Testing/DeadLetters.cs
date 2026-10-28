using Npgsql;
using RabbitMQ.Client;

namespace ProcureAi.Testing;

/// <summary>
/// Counts what Wolverine gave up on, so "nothing was dead-lettered" assertions can fail. With the RabbitMQ transport a message
/// that is moved to the error queue (no matching error rule, retries exhausted) goes to the broker's native
/// <c>wolverine-dead-letter-queue</c>, not to the <c>wolverine.wolverine_dead_letters</c> table; a table-only count is always 0.
/// Tests compare the count with its value before the action.
/// </summary>
public static class DeadLetters
{
    public const string BrokerQueue = "wolverine-dead-letter-queue";

    /// <summary>
    /// Rows of the service database's dead-letter table plus the messages in the broker's dead-letter queue. A service without a database
    /// (Notifications) passes null for <paramref name="dbConnectionString"/>: only the broker is counted.
    /// </summary>
    public static async Task<long> CountAsync(string? dbConnectionString, string rabbitConnectionString, CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
        {
            cancellationToken = TestContext.Current.CancellationToken;
        }

        var table = dbConnectionString is null ? 0 : await TableCountAsync(dbConnectionString, cancellationToken);
        return table + await BrokerCountAsync(rabbitConnectionString, cancellationToken);
    }

    private static async Task<long> TableCountAsync(string dbConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(dbConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM wolverine.wolverine_dead_letters", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> BrokerCountAsync(string rabbitConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(rabbitConnectionString) }.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            return (await channel.QueueDeclarePassiveAsync(BrokerQueue, cancellationToken)).MessageCount;
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            return 0; // the queue is declared when the first message is moved there
        }
    }
}
