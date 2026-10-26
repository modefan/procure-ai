using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Domain;

/// <summary>
/// Money held for one request. Once released it stays as a tombstone, so a reservation message that arrives after the
/// release is refused, and a second release frees nothing.
/// </summary>
public sealed class BudgetReservation
{
    private BudgetReservation()
    {
    }

    public Guid RequestId { get; private set; }

    public Money Amount { get; private set; }

    public bool IsReleased { get; private set; }

    internal static BudgetReservation Hold(Guid requestId, Money amount) =>
        new() { RequestId = requestId, Amount = amount };

    internal static BudgetReservation Tombstone(Guid requestId) =>
        new() { RequestId = requestId, Amount = Money.Zero, IsReleased = true };

    /// <summary>Marks it released and returns what it held (zero when it already was).</summary>
    internal Money Release()
    {
        if (IsReleased)
        {
            return Money.Zero;
        }

        IsReleased = true;
        return Amount;
    }
}
