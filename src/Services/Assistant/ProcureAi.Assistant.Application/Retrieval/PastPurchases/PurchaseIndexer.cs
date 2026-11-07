using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.Contracts.Approvals;
using ProcureAi.Contracts.Requests;

namespace ProcureAi.Assistant.Application.Retrieval.PastPurchases;

/// <summary>
/// A <c>RequestCompleted</c> arrived before the request's <c>RequestSubmitted</c>: it is retried on the schedule of
/// <c>Assistant:Purchases:RetryDelays</c>, in the hope the submit data arrives meanwhile.
/// </summary>
public sealed class PurchaseDataMissingException(Guid requestId)
    : Exception($"The submit data of purchase request {requestId} has not arrived yet.")
{
    public Guid RequestId { get; } = requestId;
}

/// <summary>
/// Past purchases (ruling 9): keeps the Assistant's copy of each request (<see cref="Purchase"/>) from the three events, in any order
/// (upsert by request ID), and (re)writes its purchase chunk whenever the copy is completed and has its submit data: on the completion,
/// or on a submit or approval that arrives after it. The chunk is embedded directly (no context step), with the approvers' names from
/// Access and the completion date in <c>Assistant:Purchases:TimeZone</c>; an existing chunk of the request is replaced. Rejected and
/// cancelled requests never complete, so they are never indexed. Runs inside the handler's transaction (the DbContext is passed in):
/// a lost race on the row version or the insert is retried by the messaging policy.
/// </summary>
public sealed class PurchaseIndexer(
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IAccessDirectory access,
    IAiFailures failures,
    AssistantMetrics metrics,
    IOptions<AiOptions> ai,
    IOptions<AssistantOptions> options,
    TimeProvider time,
    ILogger<PurchaseIndexer> logger)
{
    public async Task SubmittedAsync(IAssistantDbContext db, RequestSubmitted message, CancellationToken cancellationToken)
    {
        var purchase = await FindOrAddAsync(db, message.RequestId, message.TenantId, message.Number, cancellationToken);
        if (purchase is null)
        {
            return;
        }

        purchase.RecordSubmitted(
            message.Number, message.Title, message.CostCenterCode, Money.Zar(message.TotalInclVat), message.SupplierId, LinesSummary(message.Lines),
            message.SubmittedAt);
        await IndexIfCompleteAsync(db, purchase, cancellationToken);
    }

    public async Task ApprovedAsync(IAssistantDbContext db, RequestApproved message, CancellationToken cancellationToken)
    {
        var purchase = await FindOrAddAsync(db, message.RequestId, message.TenantId, message.Number, cancellationToken);
        if (purchase is null)
        {
            return;
        }

        purchase.RecordApproved(message.ApproverIds, message.OccurredAt);
        await IndexIfCompleteAsync(db, purchase, cancellationToken);
    }

    /// <param name="attempt">The delivery attempt, 1 for the first (Wolverine's <c>Envelope.Attempts</c>).</param>
    public async Task CompletedAsync(IAssistantDbContext db, RequestCompleted message, int attempt, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(db, message.RequestId, cancellationToken);
        if (existing is null || !existing.HasSubmitData)
        {
            var retries = options.Value.Purchases.RetryDelaySchedule().Length;
            if (attempt <= retries)
            {
                throw new PurchaseDataMissingException(message.RequestId);
            }

            // Kept, so a submit that still comes indexes it; the message itself ends here.
            logger.LogWarning(
                "Purchase request {RequestId} completed without its submit data after {Attempts} attempts; it is indexed when the data arrives",
                message.RequestId, attempt);
        }

        var purchase = await FindOrAddAsync(db, message.RequestId, message.TenantId, message.Number, cancellationToken);
        if (purchase is null)
        {
            return;
        }

        purchase.RecordCompleted(message.OccurredAt);
        await IndexIfCompleteAsync(db, purchase, cancellationToken);
    }

    private async Task IndexIfCompleteAsync(IAssistantDbContext db, Purchase purchase, CancellationToken cancellationToken)
    {
        if (!purchase.IsIndexable)
        {
            return;
        }

        // The purchase's embedding counts against its tenant's daily token total, whichever event (submit, approve, complete) completes it.
        using var usage = AiUsageScope.For(purchase.TenantId);

        var names = new List<string>();
        foreach (var userId in purchase.ApproverIdList)
        {
            // A user Access does not know (left the tenant) keeps their ID; a failed lookup throws and the message is retried.
            names.Add(await access.GetDisplayNameAsync(userId, purchase.TenantId, cancellationToken) ?? userId);
        }

        var zone = options.Value.Purchases.FindTimeZone() ?? TimeZoneInfo.Utc;
        var completedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(purchase.CompletedAt!.Value, zone).DateTime);
        var text = purchase.ToChunkText(names, completedOn);

        var model = ai.Value.EffectiveEmbeddingModel;
        GeneratedEmbeddings<Embedding<float>> generated;
        try
        {
            generated = await embeddings.GenerateAsync([text], cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && failures.IsTransient(exception))
        {
            throw new AiProviderUnavailableException(exception);
        }

        metrics.EmbeddingUsage(model, purchase.TenantId, generated.Usage?.InputTokenCount ?? 0, ai.Value.EffectivePrices);

        var old = await db.Chunks.IgnoreQueryFilters()
            .Where(c => c.TenantId == purchase.TenantId && c.PurchaseRequestId == purchase.RequestId)
            .ToListAsync(cancellationToken);
        db.Chunks.RemoveRange(old);
        var chunk = new Chunk(
            Guid.CreateVersion7(), purchase.TenantId, SourceType.Purchase, ChunkKind.Child, null, 0, "", text,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), time.GetUtcNow(), purchaseRequestId: purchase.RequestId);
        chunk.SetEmbedding(model, generated[0].Vector.ToArray(), null);
        db.Chunks.Add(chunk);
        logger.LogInformation("Purchase request {RequestId} indexed as a purchase chunk", purchase.RequestId);
    }

    /// <summary>The copy of the request (no caller: the tenant filter is replaced by the request ID's own row).</summary>
    private static Task<Purchase?> FindAsync(IAssistantDbContext db, Guid requestId, CancellationToken cancellationToken) =>
        db.Purchases.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.RequestId == requestId, cancellationToken);

    /// <summary>The copy of the request, a new placeholder when there is none; null when the ID belongs to another tenant (ignored).</summary>
    private async Task<Purchase?> FindOrAddAsync(
        IAssistantDbContext db, Guid requestId, string tenantId, string number, CancellationToken cancellationToken)
    {
        var purchase = db.Purchases.Local.FirstOrDefault(p => p.RequestId == requestId) ?? await FindAsync(db, requestId, cancellationToken);
        if (purchase is null)
        {
            purchase = Purchase.Placeholder(requestId, tenantId, number, time.GetUtcNow());
            db.Purchases.Add(purchase);
            return purchase;
        }

        if (purchase.TenantId != tenantId)
        {
            logger.LogWarning("An event of purchase request {RequestId} names another tenant than its copy; ignored", requestId);
            return null;
        }

        return purchase;
    }

    /// <summary>"2 x Laptop 14 inch, 1 x Docking station".</summary>
    private static string LinesSummary(IReadOnlyList<RequestLineItem> lines) =>
        string.Join(", ", lines.OrderBy(l => l.LineNo).Select(l => $"{l.Quantity} x {l.Description.Trim()}"));
}
