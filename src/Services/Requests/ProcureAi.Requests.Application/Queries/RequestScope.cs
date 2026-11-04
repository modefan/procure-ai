using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Queries;

/// <summary>
/// The one rule for which requests a caller may read, shared by the list and the single-item reads (spec 4.2, plan 07 Task 0c ruling):
/// the caller's own requests and those of the cost centers Access assigns to them (finance approvers get every cost center there;
/// managers and department heads those they manage or head). The admin API key and a service acting for nobody see all of the tenant's
/// requests (<see cref="CallerScope.All"/>), and so does a user holding the tenant-admin permission (<c>wholeTenant</c>, decided by the
/// Api). A service acting on behalf of a user is scoped like that user. The tenant itself is the query filter's.
/// </summary>
public static class RequestScope
{
    public static IQueryable<PurchaseRequest> VisibleTo(this IQueryable<PurchaseRequest> requests, CallerScope scope, bool wholeTenant)
    {
        if (scope.All || wholeTenant)
        {
            return requests;
        }

        var userId = scope.UserId;
        var codes = scope.CostCenterCodes.ToArray();
        return requests.Where(r => r.RequesterId == userId || codes.Contains(r.CostCenterCode));
    }
}
