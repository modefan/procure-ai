using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Abstractions;
using Wolverine.EntityFrameworkCore;

namespace ProcureAi.Assistant.Infrastructure.Persistence;

/// <summary>
/// Units of work on Wolverine's EF Core outbox (<see cref="IDbContextOutbox{T}"/>): each has its own scope and DbContext and an open
/// transaction, so a bulk delete (<c>ExecuteDeleteAsync</c>), the tracked changes and the outgoing messages commit together.
/// </summary>
internal sealed class AssistantUnitOfWorkFactory(IServiceScopeFactory scopes) : IAssistantUnitOfWorkFactory
{
    public async Task<IAssistantUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<AssistantDbContext>>();
            await outbox.DbContext.Database.BeginTransactionAsync(cancellationToken);
            return new AssistantUnitOfWork(scope, outbox);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class AssistantUnitOfWork(AsyncServiceScope scope, IDbContextOutbox<AssistantDbContext> outbox) : IAssistantUnitOfWork
    {
        public IAssistantDbContext Db => outbox.DbContext;

        public ValueTask PublishAsync<T>(T message) => outbox.PublishAsync(message);

        /// <summary>Saves, commits the open transaction and then sends the messages (Wolverine's outbox).</summary>
        public Task CommitAsync(CancellationToken cancellationToken) => outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

        /// <summary>Disposing the scope disposes the DbContext, which rolls back a transaction that was not committed.</summary>
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
