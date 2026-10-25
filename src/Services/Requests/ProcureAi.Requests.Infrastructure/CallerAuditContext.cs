using ProcureAi.BuildingBlocks.Persistence;
using ProcureAi.Requests.Application.Abstractions;

namespace ProcureAi.Requests.Infrastructure;

/// <summary>Gives the audit interceptor the service's caller (who and which tenant) without it knowing <see cref="ICallerContext"/>.</summary>
internal sealed class CallerAuditContext(ICallerContext caller) : IAuditContext
{
    public string UserId => caller.UserId;

    public string? TenantId => caller.TenantId;
}
