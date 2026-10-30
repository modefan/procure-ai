using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Access;
using Wolverine;

namespace ProcureAi.Access.Application.Commands;

/// <summary>Replaces the assigned cost centers of a user. ExpectedVersion is the user version the caller last saw.</summary>
public sealed record SetUserCostCenters(string UserId, IReadOnlyList<string> Codes, uint ExpectedVersion);

public static class SetUserCostCentersHandler
{
    public static async Task<UserDto> Handle(
        SetUserCostCenters command, ICallerContext caller, IAccessDbContext db, IMessageBus bus, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var user = await db.TenantUsers.SingleOrDefaultAsync(u => u.UserId == command.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {command.UserId} was not found.");
        OptimisticConcurrency.EnsureVersion($"User {command.UserId}", user.Version, command.ExpectedVersion);

        var wanted = command.Codes.Distinct(StringComparer.Ordinal).ToList();
        var known = (await db.CostCenters.AsNoTracking().Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var unknown = wanted.Where(code => !known.Contains(code)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainException($"Unknown cost center: {string.Join(", ", unknown)}.");
        }

        user.SetCostCenters(wanted);
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, "The user", cancellationToken);

        await bus.PublishAsync(new AccessChanged(
            caller.TenantId ?? throw new DomainException("A tenant is required."), SetApprovalLimitHandler.UserKind, user.UserId, time.GetUtcNow()));
        return AccessMapper.ToDto(user);
    }
}
