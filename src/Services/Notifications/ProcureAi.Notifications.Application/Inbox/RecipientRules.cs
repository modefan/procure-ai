using ProcureAi.Contracts;
using ProcureAi.Contracts.Approvals;

namespace ProcureAi.Notifications.Application.Inbox;

/// <summary>What one event tells whom: the users of the tenant who get a bell item and an email, and the text of both.</summary>
public sealed record Notice(string TenantId, Guid RequestId, IReadOnlyList<string> Recipients, string Text);

/// <summary>
/// Who hears about which event (plan 04 ruling 8): <c>ApprovalRequested</c> and <c>ApprovalReminderDue</c> go to the approvers now due
/// (an escalation opens a new step, whose <c>ApprovalRequested</c> tells the new approver: <c>ApprovalEscalated</c> itself tells nobody, so
/// one escalation is one bell item and one email); <c>RequestApproved</c>, <c>RequestRejected</c>, <c>RequestCompleted</c> and
/// <c>RequestCancellationRefused</c> to the requester. Every other event (including <c>ApprovalAlertRaised</c>, plan 06's system status)
/// tells nobody personally. An event without a recipient (a requester ID from before plan 04) tells nobody.
/// </summary>
public static class RecipientRules
{
    /// <summary>The notice for <paramref name="integrationEvent"/>, or null when it is for nobody.</summary>
    public static Notice? For(IIntegrationEvent integrationEvent)
    {
        var (requestId, recipients, text) = integrationEvent switch
        {
            ApprovalRequested e => (e.RequestId, e.ApproverIds, $"{e.Number} is waiting for your approval"),
            ApprovalReminderDue e => (e.RequestId, e.ApproverIds, $"Reminder: {Number(e.Number)} is still waiting for your approval"),
            RequestApproved e => (e.RequestId, Requester(e.RequesterId), $"Your request {e.Number} was approved"),
            RequestRejected e => (e.RequestId, Requester(e.RequesterId), $"Your request {e.Number} was rejected: {e.Reason}"),
            RequestCompleted e => (e.RequestId, Requester(e.RequesterId), $"Your request {e.Number} is complete: it has been paid in full"),
            RequestCancellationRefused e => (e.RequestId, Requester(e.RequesterId), $"The cancellation of your request {e.Number} was refused: {e.Reason}"),
            _ => (Guid.Empty, (IReadOnlyList<string>)[], ""),
        };

        var users = recipients.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        return users.Count == 0 ? null : new Notice(integrationEvent.TenantId, requestId, users, text);
    }

    /// <summary>The app link of a request (the Angular route), relative to the app.</summary>
    public static string Link(Guid requestId) => $"/requests/{requestId}";

    private static string Number(string? number) => string.IsNullOrWhiteSpace(number) ? "A request" : number;

    private static IReadOnlyList<string> Requester(string? requesterId) => requesterId is null ? [] : [requesterId];
}
