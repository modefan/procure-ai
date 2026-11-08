using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Access.Application.Commands;

/// <summary>Switches the whole demo on or off, with an optional message for the maintenance page. Setting the same state again is harmless.</summary>
public sealed record SetSystemAvailability(bool Enabled, string? Message);

public static class SetSystemAvailabilityHandler
{
    /// <summary>
    /// Writes the one <c>system_settings</c> row (creates it on the first switch). The audit interceptor records the change with the caller;
    /// the row keeps the actor and time too. No integration event: the gateway, the only reader, has no message bus and reads the switch
    /// again within its short cache duration.
    /// </summary>
    public static async Task<SystemAvailabilityDto> Handle(
        SetSystemAvailability command, ICallerContext caller, IAccessDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.SingleOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = SystemSettings.Create(command.Enabled, command.Message, caller.ActorId, time.GetUtcNow());
            db.SystemSettings.Add(settings);
        }
        else
        {
            settings.Set(command.Enabled, command.Message, caller.ActorId, time.GetUtcNow());
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (db.ChangeTracker.Entries<SystemSettings>().Any(e => e.State == EntityState.Added))
        {
            // Two first switches at once: the other one created the row. Ours is refused as a conflict; the caller sends it again.
            throw new ConflictException("The system settings were changed at the same time. Send the change again.");
        }

        return new SystemAvailabilityDto(settings.Enabled, settings.Message);
    }
}
