namespace ProcureAi.Assistant.Application.Abstractions;

/// <summary>
/// Starts short units of work for long-running jobs (the ingestion of a document) that must commit several times on the way: progress
/// and stored chunks after each batch, each commit with its events (the Wolverine outbox). A handler that takes
/// <see cref="IAssistantDbContext"/> instead gets one transaction for the whole message, which would hold every event back until the end.
/// </summary>
public interface IAssistantUnitOfWorkFactory
{
    /// <summary>A new scope with its own DbContext and an open transaction; dispose it to roll back what was not committed.</summary>
    Task<IAssistantUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

/// <summary>One transaction on the Assistant database with an outbox: what <see cref="CommitAsync"/> saves and sends goes together.</summary>
public interface IAssistantUnitOfWork : IAsyncDisposable
{
    /// <summary>The DbContext of this unit of work. No caller: the tenant filters match nothing, so queries ignore them on purpose.</summary>
    IAssistantDbContext Db { get; }

    /// <summary>Publishes an integration event once the transaction commits.</summary>
    ValueTask PublishAsync<T>(T message);

    /// <summary>Saves the changes and the outgoing messages in the transaction, commits it and sends the messages.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
