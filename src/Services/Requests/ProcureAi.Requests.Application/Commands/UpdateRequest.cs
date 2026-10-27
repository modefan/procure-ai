using Microsoft.EntityFrameworkCore;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Application.Dtos;
using ProcureAi.Requests.Application.Mapping;
using ProcureAi.Requests.Domain;

namespace ProcureAi.Requests.Application.Commands;

/// <summary>Replaces the title and all lines of a draft. <c>ExpectedVersion</c> is the version the caller last saw.</summary>
public sealed record UpdateRequest(Guid Id, uint ExpectedVersion, string Title, IReadOnlyList<LineInput> Lines);

public static class UpdateRequestHandler
{
    public static async Task<RequestDto> Handle(
        UpdateRequest command, IRequestsDbContext db, ICallerContext caller, ISupplierCatalog catalog, CancellationToken cancellationToken)
    {
        var request = await db.LoadAsync(command.Id, command.ExpectedVersion, caller, cancellationToken);
        request.UpdateTitle(command.Title);

        // A line the draft already has keeps its description and locked price, whatever the catalog says now; only SKUs new to
        // the draft are looked up (and so checked against the supplier).
        var locked = new Dictionary<string, RequestLine>(StringComparer.Ordinal);
        foreach (var existing in request.Lines)
        {
            locked.TryAdd(existing.Sku, existing);
        }

        var added = command.Lines.Select(l => l.Sku).Where(s => !locked.ContainsKey(s)).Distinct(StringComparer.Ordinal).ToList();
        var prices = await LinePricing.LookUpAsync(catalog, request.SupplierId, added, request.TenantId, cancellationToken);

        foreach (var existing in request.Lines.ToList())
        {
            request.RemoveLine(existing.LineNo);
        }

        foreach (var line in command.Lines)
        {
            if (locked.TryGetValue(line.Sku, out var kept))
            {
                request.AddLine(kept.Sku, kept.Description, line.Quantity, kept.UnitPriceExclVat);
            }
            else
            {
                LinePricing.AddFromCatalog(request, line, prices[line.Sku]);
            }
        }

        db.MarkModified(request);
        await db.SaveOrConflictAsync(cancellationToken);
        return RequestMapper.ToDto(request);
    }
}

internal static class RequestsDbContextExtensions
{
    /// <summary>
    /// Loads a request of the current tenant for change by its requester and checks the caller's version against it. Anyone else
    /// gets 403, decided before the version is checked (as in Cancel), so a stale <c>If-Match</c> does not hide the refusal.
    /// For the admin key, <c>caller.UserId</c> is the user it acts on behalf of, or <c>claude-admin</c>: the same rule as Cancel.
    /// </summary>
    public static async Task<PurchaseRequest> LoadAsync(
        this IRequestsDbContext db, Guid id, uint expectedVersion, ICallerContext caller, CancellationToken cancellationToken)
    {
        var request = await db.PurchaseRequests.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Request {id} was not found.");
        if (!string.Equals(request.RequesterId, caller.UserId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the requester can change or submit a request.");
        }

        OptimisticConcurrency.EnsureVersion($"Request {id}", request.Version, expectedVersion);

        return request;
    }

    /// <summary>Saves; a write that lost a race on the version column surfaces as a <see cref="ConcurrencyConflictException"/>.</summary>
    public static async Task SaveOrConflictAsync(this IRequestsDbContext db, CancellationToken cancellationToken)
    {
        await OptimisticConcurrency.SaveOrConflictAsync(db.SaveChangesAsync, "The request", cancellationToken);
    }
}
