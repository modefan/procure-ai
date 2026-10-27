using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Access.Application.Abstractions;
using ProcureAi.Access.Application.Dtos;
using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Access;
using Wolverine;

namespace ProcureAi.Access.Application.Commands;

/// <summary>
/// A user who signed up in the identity provider joins the demo company. Only the user ID comes from the caller, and it must be
/// the user the caller acts for; email and name are read from the identity provider.
/// </summary>
public sealed record RegisterDemoUser(string UserId);

public static class RegisterDemoUserHandler
{
    public const string RequesterRole = "requester";

    /// <summary>The longest display name and email stored (the columns of <c>tenant_users</c>).</summary>
    public const int MaxTextLength = 200;

    /// <summary>
    /// Identity provider first (organization and role; repeatable), then the tenant user in Access with the requester role,
    /// no approval limit and the sandbox cost center, then <c>AccessChanged</c> through the outbox. Someone already in Access
    /// changes nothing, so a retry after a failure in between finishes the job and a repeated call does no harm.
    /// <para>
    /// The caller may only register the user it acts for (<c>X-On-Behalf-Of-User</c>, which the gateway sets to the signed-in
    /// user): otherwise any holder of the internal service secret could put anyone into the demo company. A call without an
    /// on-behalf-of user (the bare service, or the admin API key without the header) is refused the same way.
    /// </para>
    /// </summary>
    public static async Task<RegistrationDto> Handle(
        RegisterDemoUser command, ICallerContext caller, IAccessDbContext db, IIdentityDirectory identities, IMessageBus bus,
        IOptions<DemoRegistrationOptions> options, TimeProvider time, CancellationToken cancellationToken)
    {
        if (caller.UserId != command.UserId)
        {
            throw new ForbiddenException("A user can only be registered by a call made on their own behalf.");
        }

        var tenant = identities.DemoTenantId;
        if (caller.TenantId != tenant)
        {
            throw new DomainException($"Self-registered users join the '{tenant}' company only.");
        }

        if (await db.TenantUsers.AnyAsync(u => u.UserId == command.UserId, cancellationToken))
        {
            return new RegistrationDto(tenant, Joined: false);
        }

        var sandbox = options.Value.SandboxCostCenter;
        if (!await db.CostCenters.AnyAsync(c => c.Code == sandbox, cancellationToken))
        {
            // A setup error (demo seed not run), not the caller's: nothing is changed in the identity provider.
            throw new InvalidOperationException($"The sandbox cost center '{sandbox}' does not exist in the '{tenant}' company.");
        }

        var profile = await identities.EnsureDemoMemberAsync(command.UserId, RequesterRole, cancellationToken);
        if (profile.Email.Length > MaxTextLength)
        {
            throw new DomainException($"The email address is longer than {MaxTextLength} characters.");
        }

        var name = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Email : profile.DisplayName.Trim();
        var displayName = string.IsNullOrWhiteSpace(name) ? command.UserId : name[..Math.Min(name.Length, MaxTextLength)];
        var entity = TenantUser.Create(
            tenant, command.UserId, displayName, profile.Email, [RequesterRole], Money.Zero, isFinanceApprover: false, [sandbox]);
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
