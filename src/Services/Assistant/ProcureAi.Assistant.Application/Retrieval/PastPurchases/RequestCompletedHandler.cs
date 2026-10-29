using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Contracts.Approvals;
using Wolverine;

namespace ProcureAi.Assistant.Application.Retrieval.PastPurchases;

/// <summary>
/// <c>RequestCompleted</c> (queue <c>assistant-api-requests</c>): the request is indexed as a purchase chunk (<see cref="PurchaseIndexer"/>).
/// Before its submit data it throws <see cref="PurchaseDataMissingException"/> (scheduled retries, <c>Assistant:Purchases:RetryDelays</c>);
/// on the attempt after the last retry it keeps the completion, logs and ends.
/// </summary>
public static class RequestCompletedHandler
{
    public static Task Handle(
        RequestCompleted message, IAssistantDbContext db, PurchaseIndexer indexer, Envelope envelope, CancellationToken cancellationToken) =>
        indexer.CompletedAsync(db, message, envelope.Attempts, cancellationToken);
}
