using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Access.Application.Queries;

public sealed record GetCostCenterApprovers(string CostCenterCode);

public static class GetCostCenterApproversHandler
{
    public static async Task<CostCenterApproversDto> Handle(
        GetCostCenterApprovers query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var costCenter = await db.CostCenters.AsNoTracking().SingleOrDefaultAsync(c => c.Code == query.CostCenterCode, cancellationToken)
            ?? throw new NotFoundException($"Cost center {query.CostCenterCode} was not found.");
        var department = await db.Departments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Code == costCenter.DepartmentCode, cancellationToken);
        var matrix = await db.PermissionMatrices.AsNoTracking().Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken);
        var users = await db.TenantUsers.AsNoTracking().ToListAsync(cancellationToken);

        ApproverDto? Approver(string? userId) =>
            users.FirstOrDefault(u => u.UserId == userId) is { } user ? AccessMapper.ToApprover(user, matrix) : null;

        var finance = users.Where(u => u.IsFinanceApprover).OrderBy(u => u.DisplayName).ThenBy(u => u.UserId)
            .Select(u => AccessMapper.ToApprover(u, matrix)).ToList();
        return new CostCenterApproversDto(
            costCenter.Code, costCenter.DepartmentCode, Approver(costCenter.ManagerUserId), Approver(department?.HeadUserId), finance);
    }
}
