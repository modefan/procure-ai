using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Domain;

/// <summary>One payment booked as spend on a budget. Keyed by budget and payment, so a payment is recorded once.</summary>
public sealed class BudgetSpend
{
    private BudgetSpend()
    {
    }

    public Guid BudgetId { get; private set; }

    public Guid PaymentId { get; private set; }

    public Guid RequestId { get; private set; }

    public Money Amount { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    internal static BudgetSpend Record(Guid budgetId, Guid paymentId, Guid requestId, Money amount, DateTimeOffset recordedAt) =>
        new() { BudgetId = budgetId, PaymentId = paymentId, RequestId = requestId, Amount = amount, RecordedAt = recordedAt };
}
