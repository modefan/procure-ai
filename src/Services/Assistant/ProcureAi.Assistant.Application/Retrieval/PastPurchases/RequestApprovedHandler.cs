using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Contracts.Approvals;

namespace ProcureAi.Assistant.Application.Retrieval.PastPurchases;

/// <summary><c>RequestApproved</c> (queue <c>assistant-api-requests</c>): the approvers of the Assistant's copy of the request (<see cref="PurchaseIndexer"/>).</summary>
public static class RequestApprovedHandler
{
    public static Task Handle(RequestApproved message, IAssistantDbContext db, PurchaseIndexer indexer, CancellationToken cancellationToken) =>
        indexer.ApprovedAsync(db, message, cancellationToken);
}
