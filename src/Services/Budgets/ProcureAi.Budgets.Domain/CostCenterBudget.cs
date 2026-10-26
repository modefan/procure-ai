using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Budgets.Domain;

/// <summary>
/// The budget of one cost center in one fiscal year, in Rand including VAT. Requests reserve against it; what is reserved
/// or spent is not available to others.
/// </summary>
public sealed class CostCenterBudget
{
    private readonly List<BudgetReservation> _reservations = [];

    private CostCenterBudget()
    {
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = "";

    public string CostCenterCode { get; private set; } = "";

    public FiscalYear FiscalYear { get; private set; }

    public Money Amount { get; private set; }

    /// <summary>Money already paid out of the budget. Moved here from reservations by plan 03.</summary>
    public Money Spent { get; private set; }

    public IReadOnlyList<BudgetReservation> Reservations => _reservations.AsReadOnly();

    /// <summary>
    /// Held for requests that are not released yet. It is a stored total, updated by every reserve and release, so the budget
    /// row itself changes and its version (<see cref="Version"/>) rejects the loser of two concurrent reservations.
    /// </summary>
    public Money Reserved { get; private set; }

    public Money Available => Amount - Reserved - Spent;

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public static CostCenterBudget Create(string tenantId, string costCenterCode, FiscalYear fiscalYear, Money amount)
    {
        Require(tenantId, "Tenant");
        Require(costCenterCode, "Cost center");
        EnsureNotNegative(amount);
        return new CostCenterBudget
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CostCenterCode = costCenterCode.Trim(),
            FiscalYear = fiscalYear,
            Amount = amount,
        };
    }

    /// <summary>
    /// Holds the amount for the request. The same request again gets the earlier outcome and holds nothing more; a request
    /// already released is refused as cancelled; more than is available is refused with what is available.
    /// </summary>
    public ReservationOutcome Reserve(Guid requestId, Money amount)
    {
        EnsureNotNegative(amount);
        var existing = _reservations.FirstOrDefault(r => r.RequestId == requestId);
        if (existing is not null)
        {
            return existing.IsReleased
                ? ReservationOutcome.Refused(ReservationOutcome.Cancelled, Available)
                : ReservationOutcome.Reserved(existing.Amount, Available);
        }

        if (amount.Amount > Available.Amount)
        {
            return ReservationOutcome.Refused(ReservationOutcome.OverBudget, Available);
        }

        _reservations.Add(BudgetReservation.Hold(requestId, amount));
        Reserved += amount;
        return ReservationOutcome.Reserved(amount, Available);
    }

    /// <summary>
    /// Frees what the request holds and returns it (zero when nothing is held). A request without a reservation gets a
    /// tombstone, so a reservation that arrives later is refused.
    /// </summary>
    public Money Release(Guid requestId)
    {
        var existing = _reservations.FirstOrDefault(r => r.RequestId == requestId);
        if (existing is null)
        {
            _reservations.Add(BudgetReservation.Tombstone(requestId));
            return Money.Zero;
        }

        var released = existing.Release();
        Reserved -= released;
        return released;
    }

    /// <summary>Changes the budget. It may not go below what is already reserved or spent.</summary>
    public void SetAmount(Money amount)
    {
        EnsureNotNegative(amount);
        var committed = Reserved + Spent;
        if (amount.Amount < committed.Amount)
        {
            throw new DomainException(
                $"The budget cannot be lower than the {committed.ToDisplayString()} already reserved or spent.");
        }

        Amount = amount;
    }

    private static void EnsureNotNegative(Money amount)
    {
        if (amount.Currency != Money.ZarCurrency)
        {
            throw new DomainException($"Only {Money.ZarCurrency} is supported, got {amount.Currency}.");
        }

        if (amount.Amount < 0)
        {
            throw new DomainException("An amount cannot be negative.");
        }
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }
    }
}
