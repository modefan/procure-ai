namespace ProcureAi.Requests.Domain;

/// <summary>
/// Where a request is. <c>Draft</c> and <c>Submitted</c> are Requests' own; the rest follows the approval saga (RequestApproved,
/// RequestOrdered, RequestCompleted, RequestRejected, RequestCancellationConfirmed / Refused). <c>Cancelling</c> is a requested
/// cancel of an approved or ordered request that the saga has not yet confirmed or refused (the order may be past the point of
/// no return); <c>Cancelled</c> is final. The enum is stored as text, so the order of the values does not matter.
/// </summary>
public enum RequestStatus
{
    Draft,
    Submitted,
    Approved,
    Ordered,
    Completed,
    Rejected,
    Cancelling,
    Cancelled,
}
