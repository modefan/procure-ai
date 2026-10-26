using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Domain;

/// <summary>
/// The answer to a reservation: reserved (Amount is what is held for the request), or refused (Reason says why and
/// Available is what the budget had left).
/// </summary>
public sealed record ReservationOutcome(bool IsReserved, Money Amount, Money Available, string? Reason)
{
    public const string OverBudget = "Over budget";
    public const string Cancelled = "Cancelled";

    public static ReservationOutcome Reserved(Money amount, Money available) => new(true, amount, available, null);

    public static ReservationOutcome Refused(string reason, Money available) => new(false, Money.Zero, available, reason);
}
