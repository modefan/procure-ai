namespace ProcureAi.Budgets.Domain;

/// <summary>
/// The answer to recording a payment: already recorded earlier (nothing changed), or recorded, with
/// <see cref="WithoutReservation"/> when the request held no reservation any more (none, or released), so the money was not
/// taken from a reservation.
/// </summary>
public sealed record SpendOutcome(bool AlreadyRecorded, bool WithoutReservation)
{
    public static SpendOutcome Already() => new(true, false);

    public static SpendOutcome Recorded(bool withoutReservation) => new(false, withoutReservation);
}
