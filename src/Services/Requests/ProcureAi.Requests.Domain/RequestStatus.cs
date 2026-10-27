namespace ProcureAi.Requests.Domain;

/// <summary>
/// Where a request is. <c>Draft</c> and <c>Submitted</c> are Requests' own; <c>Approved</c> and <c>Rejected</c> follow the approval
/// saga (RequestApproved / RequestRejected); <c>Cancelled</c> is the requester's.
/// </summary>
public enum RequestStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
    Cancelled,
}
