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

    /// <summary>The part of <see cref="Amount"/> already turned into spend by payments.</summary>
    public Money Spent { get; private set; }

    public bool IsReleased { get; private set; }

    /// <summary>What is still held for the request: the amount less what was spent (never below zero; zero once released).</summary>
    public Money Remaining => IsReleased || Spent.Amount >= Amount.Amount ? Money.Zero : Amount - Spent;

    internal static BudgetReservation Hold(Guid requestId, Money amount) =>
        new() { RequestId = requestId, Amount = amount };

    internal static BudgetReservation Tombstone(Guid requestId) =>
        new() { RequestId = requestId, Amount = Money.Zero, IsReleased = true };

    /// <summary>Turns up to <paramref name="amount"/> of what is still held into spend and returns what was moved.</summary>
    internal Money Spend(Money amount)
    {
        var moved = Money.Zar(Math.Min(amount.Amount, Remaining.Amount));
        Spent += moved;
        return moved;
    }

    /// <summary>Marks it released and returns what it still held (zero when it already was released or fully spent).</summary>
    internal Money Release()
    {
        if (IsReleased)
        {
            return Money.Zero;
        }

        var remaining = Remaining;
        IsReleased = true;
        return remaining;
    }
}
