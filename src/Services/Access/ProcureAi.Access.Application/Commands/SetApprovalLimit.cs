using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Access;
using Wolverine;

namespace ProcureAi.Access.Application.Commands;

/// <summary>Sets the approval limit of a user in Rand. ExpectedVersion is the user version the caller last saw.</summary>
public sealed record SetApprovalLimit(string UserId, decimal Amount, uint ExpectedVersion);

public static class SetApprovalLimitHandler
{
    public const string UserKind = "User";

    public static async Task<UserDto> Handle(
        SetApprovalLimit command, ICallerContext caller, IAccessDbContext db, IMessageBus bus, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var user = await db.TenantUsers.SingleOrDefaultAsync(u => u.UserId == command.UserId, cancellationToken)
            ?? throw new NotFoundException($"User {command.UserId} was not found.");
        if (user.Version != command.ExpectedVersion)
        {
            throw new ConcurrencyConflictException(
                $"User {command.UserId} was changed by someone else (version {user.Version}, expected {command.ExpectedVersion}).");
        }

        user.SetApprovalLimit(Money.Zar(command.Amount));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException($"The user was changed by someone else. {exception.Message}");
        }

        await bus.PublishAsync(new AccessChanged(
            caller.TenantId ?? throw new DomainException("A tenant is required."), UserKind, user.UserId, time.GetUtcNow()));
        return AccessMapper.ToDto(user);
    }
}
