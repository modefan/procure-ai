using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Application.Abstractions;
using ProcureAi.Approvals.Application.Dtos;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Application.Queries;

/// <summary>The open steps the caller is an approver of, in the caller's tenant: the most urgent (earliest due) first.</summary>
public sealed record GetInbox(int Page, int PageSize);

public static class GetInboxHandler
{
    public static async Task<PagedResult<ApprovalStepDto>> Handle(
        GetInbox query, IApprovalsDbContext db, ICallerContext caller,
        CancellationToken cancellationToken)
    {
        var userId = caller.UserId;
        // Open = pending and opened (a due date); the tenant filter of the context limits it to the caller's tenant.
        var open = db.Steps.AsNoTracking()
            .Where(s => s.Status == ApprovalStepStatus.Pending && s.DueAt != null)
            .Where(s => EF.Property<string[]>(s, "_approverIds").Contains(userId));
        var total = await open.CountAsync(cancellationToken);
        var page = await open
            .OrderBy(s => s.DueAt)
            .ThenBy(s => s.RequestNumber)
            .ThenBy(s => s.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<ApprovalStepDto>([.. page.Select(s => ApprovalStepView.ToDto(s, userId))], query.Page, query.PageSize, total);
    }
}
