using Microsoft.EntityFrameworkCore;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;

namespace ProcureAi.Assistant.Application.Documents;

/// <summary>The caller's tenant's policy documents that were not removed, by title.</summary>
public sealed record ListPolicyDocuments(int Page, int PageSize);

/// <summary>One policy document of the caller's tenant; a removed one or one of another tenant is not found.</summary>
public sealed record GetPolicyDocument(Guid Id);

public static class ListPolicyDocumentsHandler
{
    public static async Task<PagedResult<PolicyDocumentDto>> Handle(
        ListPolicyDocuments query, IAssistantDbContext db, IAccessDirectory access, CancellationToken cancellationToken)
    {
        // The tenant filter of the context limits it to the caller's tenant.
        var visible = db.PolicyDocuments.AsNoTracking().Where(d => !d.Removed);
        var total = await visible.CountAsync(cancellationToken);
        var page = await visible.OrderBy(d => d.Title).ThenBy(d => d.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<PolicyDocumentDto>(
            await PolicyDocumentView.ToDtosAsync(page, access, cancellationToken), query.Page, query.PageSize, total);
    }
}

public static class GetPolicyDocumentHandler
{
    public static async Task<PolicyDocumentDto> Handle(
        GetPolicyDocument query, IAssistantDbContext db, IAccessDirectory access, CancellationToken cancellationToken)
    {
        var document = await db.PolicyDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == query.Id && !d.Removed, cancellationToken)
            ?? throw new NotFoundException($"Policy document {query.Id} was not found.");
        return await PolicyDocumentView.ToDtoAsync(document, access, cancellationToken);
    }
}
