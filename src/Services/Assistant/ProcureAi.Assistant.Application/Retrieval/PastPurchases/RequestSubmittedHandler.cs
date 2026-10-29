using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Assistant.Application.Retrieval.PastPurchases;

/// <summary>
/// <c>RequestSubmitted</c> (queue <c>assistant-api-requests</c>): the title, cost center, total, supplier and lines of the Assistant's copy of
/// the request (<see cref="PurchaseIndexer"/>). One transaction with the durable inbox, so a redelivery is dropped.
/// </summary>
public static class RequestSubmittedHandler
{
    public static Task Handle(RequestSubmitted message, IAssistantDbContext db, PurchaseIndexer indexer, CancellationToken cancellationToken) =>
        indexer.SubmittedAsync(db, message, cancellationToken);
}
