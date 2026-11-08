using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;

namespace ProcureAi.Access.Application.Queries;

/// <summary>Is the demo switched on (the system-wide switch, not tenant data)?</summary>
public sealed record GetSystemAvailability;

public static class GetSystemAvailabilityHandler
{
    /// <summary>The stored switch; on (<see cref="SystemSettings.Default"/>) when nobody ever switched it.</summary>
    public static async Task<SystemAvailabilityDto> Handle(GetSystemAvailability query, IAccessDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, cancellationToken) ?? SystemSettings.Default;
        return new SystemAvailabilityDto(settings.Enabled, settings.Message);
    }
}
