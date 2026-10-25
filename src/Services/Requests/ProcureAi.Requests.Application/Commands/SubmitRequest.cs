using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;
using Wolverine;

namespace ProcureAi.Requests.Application.Commands;

public sealed record SubmitRequest(Guid Id, uint ExpectedVersion);

public static class SubmitRequestHandler
{
    /// <summary>
    /// Submits the draft and publishes <c>RequestSubmitted</c>. Wolverine's EF Core transaction middleware commits
    /// the change and the outgoing message together (transactional outbox).
    /// </summary>
    public static async Task Handle(
        SubmitRequest command,
        IRequestsDbContext db,
        IMessageBus bus,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var request = await db.LoadAsync(command.Id, command.ExpectedVersion, cancellationToken);
        request.Submit(time.GetUtcNow());
        await db.SaveOrConflictAsync(cancellationToken);

        foreach (var submitted in request.DomainEvents.OfType<PurchaseRequestSubmitted>())
        {
            await bus.PublishAsync(RequestMapper.ToIntegrationEvent(submitted));
        }

        request.ClearDomainEvents();
    }
}
