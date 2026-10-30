using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Web.Status;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Wolverine.Runtime;

namespace ProcureAi.BuildingBlocks.Messaging.Status;

/// <summary>
/// The status snapshot's message counts: Wolverine's message store (<c>IMessageStore.Admin.FetchCountsAsync()</c>: incoming, outgoing,
/// scheduled, dead letters; zero for a service without a store) plus the depth of the broker queue <see cref="BrokerDeadLetterQueue"/>.
/// With the RabbitMQ transport a message moved to the error queue lands there, not in the dead-letter table (CLAUDE.md learning
/// 2026-10-28), so a table-only count would always show 0. One broker connection is kept open and replaced when it fails.
/// </summary>
public sealed class WolverineCounts(IWolverineRuntime runtime, IConfiguration configuration, ILogger<WolverineCounts> logger)
    : IWolverineCounts, IAsyncDisposable
{
    /// <summary>Wolverine's native dead-letter queue on RabbitMQ (one per broker, shared by every service).</summary>
    public const string BrokerDeadLetterQueue = "wolverine-dead-letter-queue";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<WolverineCountsDto> FetchAsync(CancellationToken cancellationToken)
    {
        long incoming = 0, outgoing = 0, scheduled = 0, deadLetters = 0;
        try
        {
            var counts = await runtime.Storage.Admin.FetchCountsAsync();
            (incoming, outgoing, scheduled, deadLetters) = (counts.Incoming, counts.Outgoing, counts.Scheduled, counts.DeadLetter);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not read Wolverine's message store counts");
        }

        return new WolverineCountsDto(incoming, outgoing, scheduled, deadLetters, await BrokerDeadLettersAsync(cancellationToken));
    }

    /// <summary>Messages in <see cref="BrokerDeadLetterQueue"/>; 0 before the queue exists (declared when the first message is moved there) or when the broker is not reachable.</summary>
    public async Task<long> BrokerDeadLettersAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(MessagingSetup.RabbitMqConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not { IsOpen: true })
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                }

                _connection = await new ConnectionFactory { Uri = new Uri(connectionString), ClientProvidedName = "status-dead-letters" }
                    .CreateConnectionAsync(cancellationToken);
            }

            await using var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
            try
            {
                return (await channel.QueueDeclarePassiveAsync(BrokerDeadLetterQueue, cancellationToken)).MessageCount;
            }
            catch (OperationInterruptedException)
            {
                return 0; // no such queue yet
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not read the depth of {Queue}", BrokerDeadLetterQueue);
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
