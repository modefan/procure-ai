using Microsoft.Extensions.Configuration;
using ProcureAi.BuildingBlocks.Messaging.Status;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Npgsql;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>The messaging half of a service's demo reset (plan 06 ruling 4).</summary>
public static class WolverineDemoReset
{
    /// <summary>Wolverine's schema of its Postgres message store (the default of <c>PersistMessagesWithPostgresql</c>; Audit names it the same).</summary>
    public const string MessageStoreSchema = "wolverine";

    /// <summary>The three deletes of <see cref="ClearEnvelopesAsync"/>; an EF Core service runs them on its own DbContext (its connection).</summary>
    public const string ClearEnvelopesSql =
        $"""
        DELETE FROM {MessageStoreSchema}.wolverine_incoming_envelopes;
        DELETE FROM {MessageStoreSchema}.wolverine_outgoing_envelopes;
        DELETE FROM {MessageStoreSchema}.wolverine_dead_letters;
        """;

    /// <summary>
    /// Deletes every envelope of the service's message store: incoming (also scheduled saga timeouts), outgoing and dead letters, with plain
    /// deletes on the three envelope tables. Not <c>IMessageStoreAdmin.ClearAllAsync</c>: on a running node that also clears Wolverine's node
    /// records, which its agents keep using. A message still on the broker at this moment can arrive afterwards and refer to deleted rows; it
    /// fails and is dead-lettered (the accepted cost of ruling 4).
    /// </summary>
    public static async Task ClearEnvelopesAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ClearEnvelopesSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Messages waiting in each of <paramref name="queues"/> on the broker (0 for a queue that does not exist yet).</summary>
    public static async Task<long> QueueDepthAsync(IConfiguration configuration, IEnumerable<string> queues, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(MessagingSetup.RabbitMqConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return 0;
        }

        var factory = new ConnectionFactory { Uri = new Uri(connectionString), ClientProvidedName = "demo-reset" };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        long depth = 0;
        foreach (var queue in queues)
        {
            // A failed passive declare closes the channel: one channel per queue.
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            try
            {
                depth += (await channel.QueueDeclarePassiveAsync(queue, cancellationToken)).MessageCount;
            }
            catch (OperationInterruptedException)
            {
                // not declared yet
            }
        }

        return depth;
    }

    /// <summary>
    /// Empties the broker's shared dead-letter queue <see cref="WolverineCounts.BrokerDeadLetterQueue"/> (with RabbitMQ, a message moved to
    /// the error queue lands there, not in the store's table). Nothing to do when it does not exist yet. Returns how many were purged.
    /// </summary>
    public static async Task<uint> PurgeBrokerDeadLettersAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(MessagingSetup.RabbitMqConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return 0;
        }

        var factory = new ConnectionFactory { Uri = new Uri(connectionString), ClientProvidedName = "demo-reset" };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            return await channel.QueuePurgeAsync(WolverineCounts.BrokerDeadLetterQueue, cancellationToken);
        }
        catch (OperationInterruptedException)
        {
            return 0; // not declared yet: nothing was ever dead-lettered
        }
    }
}
