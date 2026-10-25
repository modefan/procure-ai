using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Riok.Mapperly.Abstractions;

namespace ProcureAi.Access.Application.Mapping;

[Mapper]
public static partial class AccessMapper
{
    [MapProperty("ApprovalLimit.Amount", nameof(UserDto.ApprovalLimit))]
    [MapperIgnoreSource(nameof(TenantUser.TenantId))]
    public static partial UserDto ToDto(TenantUser user);

    public static MatrixDto ToDto(PermissionMatrix matrix) => new(
        matrix.TenantId,
        matrix.Version,
        [.. Permissions.All.Order(StringComparer.Ordinal)],
        [.. matrix.Roles.Select(role => new RoleDto(role, [.. matrix.PermissionsOf(role).Order(StringComparer.Ordinal)]))]);

    public static EffectiveAccessDto ToDto(EffectiveAccess access) => new(
        [.. access.Permissions.Order(StringComparer.Ordinal)], access.CostCenterCodes, access.ApprovalLimit.Amount);

    public static ApproverDto ToApprover(TenantUser user, PermissionMatrix? matrix) =>
        new(user.UserId, user.DisplayName, user.ApprovalLimit.Amount, AccessResolver.CanApprove(matrix, user));
}
