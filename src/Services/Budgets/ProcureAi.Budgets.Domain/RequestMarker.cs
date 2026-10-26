namespace ProcureAi.Budgets.Domain;

/// <summary>
/// One row per request that was reserved or released, keyed by tenant and request (a unique key). Reserve and release both
/// write it, so a reservation and a release of the same request that run at the same time conflict on this one row: the
/// loser fails, is retried, and then sees the other's outcome. Without it the two would touch different rows and a
/// reservation could survive its release. It also remembers a release that came before the reservation (the release message
/// carries no cost center, so no budget could hold that), and the reservation that follows is refused as cancelled.
/// </summary>
public sealed class RequestMarker
{
    private RequestMarker()
    {
    }

    public string TenantId { get; private set; } = "";

    public Guid RequestId { get; private set; }

    /// <summary>True once the request was released: a reservation for it is refused.</summary>
    public bool IsReleased { get; private set; }

    public static RequestMarker Reserved(string tenantId, Guid requestId) =>
        new() { TenantId = tenantId, RequestId = requestId };

    public static RequestMarker Released(string tenantId, Guid requestId) =>
        new() { TenantId = tenantId, RequestId = requestId, IsReleased = true };

    public void Release() => IsReleased = true;
}
