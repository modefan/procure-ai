using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

/// <summary>
/// A user of a tenant, with the data authorization needs besides the token: approval limit, finance-approver flag, assigned
/// cost centers and the roles the user holds (used only to decide who is an eligible approver; a user's own permissions
/// come from the token).
/// </summary>
public sealed class TenantUser
{
    private readonly List<string> _roles = [];
    private readonly List<string> _costCenterCodes = [];

    private TenantUser()
    {
    }

    public string TenantId { get; private set; } = "";

    public string UserId { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    public string Email { get; private set; } = "";

    public IReadOnlyList<string> Roles => _roles.AsReadOnly();

    public Money ApprovalLimit { get; private set; }

    public bool IsFinanceApprover { get; private set; }

    public IReadOnlyList<string> CostCenterCodes => _costCenterCodes.AsReadOnly();

    /// <summary>Optimistic concurrency token (the Postgres <c>xmin</c> system column), set by the database on every write.</summary>
    public uint Version { get; private set; }

    public static TenantUser Create(
        string tenantId, string userId, string displayName, string email, IEnumerable<string> roles,
        Money approvalLimit, bool isFinanceApprover, IEnumerable<string> costCenterCodes)
    {
        Require(tenantId, "Tenant");
        Require(userId, "User");
        Require(displayName, "Display name");
        var user = new TenantUser
        {
            TenantId = tenantId,
            UserId = userId,
            DisplayName = displayName.Trim(),
            Email = email.Trim(),
            IsFinanceApprover = isFinanceApprover,
        };
        user._roles.AddRange(roles.Distinct(StringComparer.Ordinal));
        user._costCenterCodes.AddRange(costCenterCodes.Distinct(StringComparer.Ordinal));
        user.SetApprovalLimit(approvalLimit);
        return user;
    }

    public void SetApprovalLimit(Money limit)
    {
        if (limit.Currency != Money.ZarCurrency)
        {
            throw new DomainException($"Only {Money.ZarCurrency} is supported, got {limit.Currency}.");
        }

        if (limit.Amount < 0)
        {
            throw new DomainException("The approval limit cannot be negative.");
        }

        ApprovalLimit = limit;
    }

    /// <summary>Replaces the assigned cost centers (duplicates dropped, order kept).</summary>
    public void SetCostCenters(IEnumerable<string> codes)
    {
        var wanted = codes.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainException("A cost center code cannot be empty.");
        }

        _costCenterCodes.Clear();
        _costCenterCodes.AddRange(wanted);
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }
    }
}
