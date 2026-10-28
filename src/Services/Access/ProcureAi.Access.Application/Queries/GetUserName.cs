using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Access.Application.Queries;

public sealed record GetUserName(string UserId);

public static class GetUserNameHandler
{
    /// <summary>The display name and email address of a user of the caller tenant (the tenant query filter applies); 404 for anyone else.</summary>
    public static async Task<UserNameDto> Handle(GetUserName query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.TenantUsers.AsNoTracking()
            .Where(u => u.UserId == query.UserId)
            .Select(u => new UserNameDto(u.UserId, u.DisplayName, u.Email))
            .SingleOrDefaultAsync(cancellationToken);
        return user ?? throw new NotFoundException($"User {query.UserId} was not found.");
    }
}
