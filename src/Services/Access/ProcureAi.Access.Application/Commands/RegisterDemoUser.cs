using Microsoft.EntityFrameworkCore;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Access;
using Wolverine;

namespace ProcureAi.Access.Application.Commands;

/// <summary>A user who signed up in the identity provider joins the demo company. The facts come from the user's own token at the gateway.</summary>
public sealed record RegisterDemoUser(string UserId, string Email, string DisplayName);

public static class RegisterDemoUserHandler
{
    public const string RequesterRole = "requester";

    /// <summary>
    /// Identity provider first (organization and role; repeatable), then the tenant user in Access with the requester role,
    /// no approval limit and no cost center, then <c>AccessChanged</c> through the outbox. Someone already in Access changes
    /// nothing, so a retry after a failure in between finishes the job and a repeated call does no harm.
    /// </summary>
    public static async Task<RegistrationDto> Handle(
        RegisterDemoUser command, ICallerContext caller, IAccessDbContext db, IIdentityDirectory identities, IMessageBus bus,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var tenant = identities.DemoTenantId;
        if (caller.TenantId != tenant)
        {
            throw new DomainException($"Self-registered users join the '{tenant}' company only.");
        }

        if (await db.TenantUsers.AnyAsync(u => u.UserId == command.UserId, cancellationToken))
        {
            return new RegistrationDto(tenant, Joined: false);
        }

        await identities.EnsureDemoMemberAsync(command.UserId, RequesterRole, cancellationToken);

        var displayName = new[] { command.DisplayName, command.Email, command.UserId }.First(v => !string.IsNullOrWhiteSpace(v));
        var entity = TenantUser.Create(
            tenant, command.UserId, displayName, command.Email ?? "", [RequesterRole], Money.Zero, isFinanceApprover: false, []);
        db.TenantUsers.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A parallel call inserted the same user first (primary key tenant + user). Whoever lost has nothing left to do.
            // The re-check below works because Wolverine's EF middleware runs in Lightweight mode: no transaction is open during the
            // handler. Under Eager mode the failed INSERT would abort the transaction (Postgres 25P02) and the re-check would fail too.
            // Clear everything tracked: the audit interceptor added an AuditEntry inside the failed save, and Wolverine's middleware
            // saves again after the handler, which would write a phantom "Insert tenant user" row. Nothing else is tracked yet
            // (the reads above do not track, and the event is published only after the save).
            db.ChangeTracker.Clear();
            if (await db.TenantUsers.AnyAsync(u => u.UserId == command.UserId, cancellationToken))
            {
                return new RegistrationDto(tenant, Joined: false);
            }

            throw;
        }

        await bus.PublishAsync(new AccessChanged(tenant, SetApprovalLimitHandler.UserKind, command.UserId, time.GetUtcNow()));
        return new RegistrationDto(tenant, Joined: true);
    }
}
