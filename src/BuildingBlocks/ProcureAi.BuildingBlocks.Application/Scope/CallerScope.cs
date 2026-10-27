using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// What a caller may reach: everything (<paramref name="All"/>: the admin API key, and a service acting for nobody) or their own
/// requests plus the cost centers Access assigns to them (finance approvers get every cost center there). A service acting on behalf of a user is scoped like that user.
/// </summary>
public sealed record CallerScope(bool All, string UserId, IReadOnlySet<string> CostCenterCodes)
{
    /// <summary>True when the caller sees everything, made the request themselves, or acts for the cost center.</summary>
    public bool Allows(string requesterId, string costCenterCode) =>
        All
        || string.Equals(requesterId, UserId, StringComparison.Ordinal)
        || CostCenterCodes.Contains(costCenterCode);

    /// <summary>True when the caller sees everything or acts for the cost center.</summary>
    public bool AllowsCostCenter(string costCenterCode) => All || CostCenterCodes.Contains(costCenterCode);
}

/// <summary>Builds the <see cref="CallerScope"/> of the current caller from the caller context and Access (cached there).</summary>
public sealed class CallerScopeResolver(ICallerContext caller, IAccessDirectory access) : ICallerScope
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public async Task<CallerScope> GetAsync(CancellationToken cancellationToken)
    {
        // The admin key sees everything; so does a service acting for nobody (no user to scope to). A service acting on behalf
        // of a user gets that user's scope from Access, like the user would.
        if (caller.Kind is CallerKind.AdminApiKey
            || (caller.Kind is CallerKind.Service && caller.UserId == SystemCaller.Id))
        {
            return new CallerScope(true, caller.UserId, None);
        }

        if (caller.TenantId is not { } tenant)
        {
            return new CallerScope(false, caller.UserId, None);
        }

        // The cost centers do not depend on roles, so Access is asked without them.
        var effective = await access.GetEffectiveAccessAsync(caller.UserId, [], tenant, cancellationToken);
        return new CallerScope(false, caller.UserId, effective.CostCenterCodes.ToHashSet(StringComparer.Ordinal));
    }
}
