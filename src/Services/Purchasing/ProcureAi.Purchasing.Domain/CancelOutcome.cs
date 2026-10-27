namespace ProcureAi.Purchasing.Domain;

/// <summary>What asking an order to cancel led to.</summary>
public abstract record CancelOutcome
{
    private CancelOutcome()
    {
    }

    /// <summary>The order is now cancelled. <paramref name="SupplierMustBeTold"/>: the supplier has (or may have) it, so tell them.</summary>
    public sealed record Cancelled(bool SupplierMustBeTold) : CancelOutcome;

    /// <summary>It was cancelled before; nothing changed.</summary>
    public sealed record AlreadyCancelled : CancelOutcome;

    /// <summary>It cannot be cancelled any more.</summary>
    public sealed record Refused(string Reason) : CancelOutcome;
}
