namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// Human-readable numbers (PR-1043, PO-2007, ...): sequential per tenant and name, starting at 1, safe under concurrent
/// callers across instances. It runs in the caller's database transaction, so a failed save gives the number back (no gaps).
/// </summary>
public interface ITenantSequence
{
    /// <summary>The next value of the sequence <paramref name="name"/> of <paramref name="tenantId"/>.</summary>
    Task<int> NextAsync(string tenantId, string name, CancellationToken cancellationToken);
}
