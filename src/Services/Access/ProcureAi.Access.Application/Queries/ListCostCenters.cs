using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;

namespace ProcureAi.Access.Application.Queries;

public sealed record ListCostCenters;

public static class ListCostCentersHandler
{
    /// <summary>All cost centers of the tenant with department, manager and department head names (the tenant query filter applies).</summary>
    public static async Task<IReadOnlyList<CostCenterAdminDto>> Handle(ListCostCenters query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var costCenters = await db.CostCenters.AsNoTracking().OrderBy(c => c.Code).ToListAsync(cancellationToken);
        var departments = (await db.Departments.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(d => d.Code, StringComparer.Ordinal);
        var names = await db.TenantUsers.AsNoTracking().ToDictionaryAsync(u => u.UserId, u => u.DisplayName, StringComparer.Ordinal, cancellationToken);

        string? NameOf(string? userId) => userId is not null && names.TryGetValue(userId, out var name) ? name : null;

        return [.. costCenters.Select(c =>
        {
            departments.TryGetValue(c.DepartmentCode, out var department);
            return new CostCenterAdminDto(
                c.Code, c.Name, c.DepartmentCode, department?.Name ?? c.DepartmentCode, c.ManagerUserId, NameOf(c.ManagerUserId),
                department?.HeadUserId, NameOf(department?.HeadUserId));
        })];
    }
}
