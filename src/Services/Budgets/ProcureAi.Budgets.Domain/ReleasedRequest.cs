namespace ProcureAi.Budgets.Domain;

/// <summary>
/// A tenant-level tombstone: the request was released before its reservation arrived (the release message carries no cost
/// center, so no budget could hold the tombstone). A reservation for it is refused as cancelled.
/// </summary>
public sealed class ReleasedRequest
{
    private ReleasedRequest()
    {
    }

    public string TenantId { get; private set; } = "";

    public Guid RequestId { get; private set; }

    public static ReleasedRequest Create(string tenantId, Guid requestId) =>
        new() { TenantId = tenantId, RequestId = requestId };
}
