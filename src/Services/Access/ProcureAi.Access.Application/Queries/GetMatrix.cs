using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Application.Mapping;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Access.Application.Queries;

public sealed record GetMatrix;

public static class GetMatrixHandler
{
    public static async Task<MatrixDto> Handle(GetMatrix query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var matrix = await db.PermissionMatrices.AsNoTracking().Include(m => m.Grants).SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("This tenant has no permission matrix.");
        return AccessMapper.ToDto(matrix);
    }
}
