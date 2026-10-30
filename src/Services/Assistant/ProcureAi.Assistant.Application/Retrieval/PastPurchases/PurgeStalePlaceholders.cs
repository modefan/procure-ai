using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Abstractions;

namespace ProcureAi.Assistant.Application.Retrieval.PastPurchases;

/// <summary>
/// Sent every <c>Assistant:Purchases:PlaceholderCleanupInterval</c>: deletes the placeholder purchases (an approval or completion
/// arrived but the <c>RequestSubmitted</c> never did) older than <c>Assistant:Purchases:PlaceholderMaxAge</c>. A placeholder holds no
/// submit data and can never be indexed, so it only wastes a row; a purchase with its submit data is never touched.
/// </summary>
public sealed record PurgeStalePlaceholders;

public static class PurgeStalePlaceholdersHandler
{
    public static async Task Handle(
        PurgeStalePlaceholders message, IAssistantDbContext db, IOptions<AssistantOptions> options, TimeProvider time,
        ILogger<PurgeStalePlaceholders> logger, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - options.Value.Purchases.PlaceholderMaxAge;
        // A background job has no caller, so the tenant filter would match nothing: this cleanup is for every tenant.
        var deleted = await db.Purchases.IgnoreQueryFilters()
            .Where(p => p.SubmittedAt == null && p.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} placeholder purchases created before {Cutoff}", deleted, cutoff);
        }
    }
}
