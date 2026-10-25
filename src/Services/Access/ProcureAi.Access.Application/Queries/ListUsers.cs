using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;

namespace ProcureAi.Access.Application.Queries;

public sealed record ListUsers;

public static class ListUsersHandler
{
    /// <summary>All users of the tenant (a tenant has tens of users here, so no paging).</summary>
    public static async Task<IReadOnlyList<UserDto>> Handle(ListUsers query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var users = await db.TenantUsers.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.UserId).ToListAsync(cancellationToken);
        return [.. users.Select(AccessMapper.ToDto)];
    }
}
