using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Access;
using Wolverine;

namespace ProcureAi.Access.Application.Commands;

/// <summary>Replaces the permissions of one role in the tenant of the caller. ExpectedVersion is the matrix version the caller last saw.</summary>
public sealed record SetRolePermissions(string Role, IReadOnlyList<string> Permissions, uint ExpectedVersion);

public static class SetRolePermissionsHandler
{
    public const string MatrixKind = "Matrix";

    /// <summary>Changes the matrix and publishes AccessChanged in the same transaction (transactional outbox).</summary>
    public static async Task<MatrixDto> Handle(
        SetRolePermissions command, ICallerContext caller, IAccessDbContext db, IMessageBus bus, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var matrix = await db.PermissionMatrices.Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("This tenant has no permission matrix.");
        OptimisticConcurrency.EnsureVersion("The matrix", matrix.Version, command.ExpectedVersion);

        matrix.SetRolePermissions(command.Role, command.Permissions, time.GetUtcNow());
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, "The matrix", cancellationToken);

        await bus.PublishAsync(new AccessChanged(
            caller.TenantId ?? throw new DomainException("A tenant is required."), MatrixKind, null, time.GetUtcNow()));
        return AccessMapper.ToDto(matrix);
    }
}
