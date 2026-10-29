using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application.Caching;
using ProcureAi.Contracts.Assistant;
using Wolverine;

namespace ProcureAi.Assistant.Application.Ingestion;

/// <summary>
/// Handles <see cref="IngestPolicyDocument"/> from the local durable queue. Not a transactional handler (it takes no DbContext): the job
/// commits after each step (<see cref="PolicyIngestion"/>), so progress events leave while it runs.
/// </summary>
public static class IngestPolicyDocumentHandler
{
    public static Task Handle(IngestPolicyDocument message, PolicyIngestion ingestion, Envelope envelope, CancellationToken cancellationToken) =>
        ingestion.RunAsync(message, envelope.Attempts, cancellationToken);
}

/// <summary>
/// The ingestion of one version of a policy document:
/// <list type="number">
/// <item>extract the text (<see cref="ITextExtractor"/>); an unreadable file is <c>Failed</c> with a user-safe reason;</item>
/// <item>the document guard (<see cref="IPromptShield.CheckDocumentAsync"/>): a flagged document is <c>Flagged</c>, nothing is indexed;</item>
/// <item>plan the chunks (<see cref="ChunkPlanner"/>) and store the parents;</item>
/// <item>children whose content hash the tenant already embedded with this model copy that context and vector (<see cref="EmbeddingReuse"/>);
/// the others get a context (<see cref="ContextWriter"/>, unless <c>Assistant:Ingestion:ContextualRetrieval</c> is false) and are
/// embedded in batches of <c>Assistant:Ingestion:BatchSize</c> (<c>context + "\n" + text</c>);</item>
/// <item>after each batch the chunks and the progress commit together, with a <c>PolicyDocumentProgressed</c> at each new 10 % step;</item>
/// <item>at the end the version becomes current in one transaction (<c>current_version</c> switched, the older versions' chunks deleted),
/// so retrieval sees either the old version or the new one, never both or neither.</item>
/// </list>
/// Every status change publishes <c>PolicyDocumentStatusChanged</c> (outbox). A retry resumes: the children a failed attempt already stored
/// are kept. A version that is no longer the newest (replaced again, or the document removed) stops at its next commit and deletes what it
/// stored. A provider failure that may pass is rethrown as <see cref="AiProviderUnavailableException"/> (scheduled retries,
/// <c>Assistant:Ingestion:RetryDelays</c>); on the last attempt the version is <c>Failed</c> with the reason.
/// </summary>
public sealed class PolicyIngestion(
    IAssistantUnitOfWorkFactory units,
    IEnumerable<ITextExtractor> extractors,
    ChunkPlanner planner,
    IPromptShield shield,
    ContextWriter contextWriter,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IAiFailures failures,
    ICacheInvalidator cache,
    AssistantMetrics metrics,
    IOptions<AiOptions> ai,
    IOptions<AssistantOptions> options,
    TimeProvider time,
    ILogger<PolicyIngestion> logger)
{
    /// <summary>How often a commit that lost a race on the document row (a replace or remove meanwhile) is tried again.</summary>
    private const int CommitAttempts = 3;

    private const string SectionSeparator = " › ";

    private sealed record Job(Guid DocumentId, int Version, string TenantId, string Title, string ContentType, byte[] Content, int ProgressStep);

    /// <param name="attempt">The delivery attempt, 1 for the first (Wolverine's <see cref="Envelope.Attempts"/>).</param>
    public async Task RunAsync(IngestPolicyDocument message, int attempt, CancellationToken cancellationToken)
    {
        var job = await StartAsync(message, cancellationToken);
        if (job is null)
        {
            return;
        }

        try
        {
            await IngestAsync(job, cancellationToken);
        }
        catch (TextExtractionException exception)
        {
            logger.LogWarning(exception, "Policy document {DocumentId} version {Version} could not be read", job.DocumentId, job.Version);
            await EndAsync(job, PolicyDocumentStatus.Failed, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && failures.IsTransient(exception))
        {
            var retries = options.Value.Ingestion.RetryDelaySchedule().Length;
            if (attempt <= retries)
            {
                logger.LogWarning(
                    exception, "The AI model failed for policy document {DocumentId} version {Version}, attempt {Attempt}; retrying",
                    job.DocumentId, job.Version, attempt);
                throw new AiProviderUnavailableException(exception);
            }

            logger.LogError(
                exception, "The AI model failed for policy document {DocumentId} version {Version} after {Attempts} attempts; giving up",
                job.DocumentId, job.Version, attempt);
            await EndAsync(
                job, PolicyDocumentStatus.Failed,
                $"The AI model could not be reached ({exception.GetType().Name}) after {attempt} attempts. Upload the file again later.",
                cancellationToken);
        }
    }

    /// <summary>Loads the version to ingest and moves it to <c>Checking</c>; null when there is nothing (left) to do.</summary>
    private async Task<Job?> StartAsync(IngestPolicyDocument message, CancellationToken cancellationToken)
    {
        Job? job = null;
        await ChangeAsync(message.DocumentId, message.Version, async (unit, document) =>
        {
            job = null;
            if (document.Status is PolicyDocumentStatus.Ready or PolicyDocumentStatus.Flagged or PolicyDocumentStatus.Failed)
            {
                return; // a redelivery of a version that already ended
            }

            var file = await unit.Db.PolicyDocumentFiles.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(f => f.DocumentId == message.DocumentId && f.Version == message.Version, cancellationToken);
            if (file is null)
            {
                await EndInAsync(unit, document, message.Version, PolicyDocumentStatus.Failed, "The uploaded file is missing.", cancellationToken);
                return;
            }

            // A retry finds it checking or embedding already: it carries on from there.
            if (document.Status == PolicyDocumentStatus.Uploaded && document.StartChecking(message.Version, time.GetUtcNow()))
            {
                await PublishStatusAsync(unit, document, message.Version);
            }

            job = new Job(document.Id, message.Version, document.TenantId, document.Title, file.ContentType, file.Content, document.Progress / 10 * 10);
        }, cancellationToken);
        return job;
    }

    private async Task IngestAsync(Job job, CancellationToken cancellationToken)
    {
        var extractor = extractors.FirstOrDefault(e => e.Supports(job.ContentType))
            ?? throw new TextExtractionException("This type of file cannot be read.");
        ExtractedDocument extracted;
        using (var stream = new MemoryStream(job.Content, writable: false))
        {
            extracted = extractor.Extract(stream);
        }

        if (extracted.Sections.Count == 0)
        {
            throw new TextExtractionException("The file has no text to index. A scanned PDF needs text recognition first.");
        }

        var guard = await shield.CheckDocumentAsync(string.Join("\n\n", extracted.Sections.Select(s => s.Text)), cancellationToken);
        if (guard.Blocked)
        {
            var reason = string.IsNullOrWhiteSpace(guard.Reason) ? "The document contains instructions to the assistant." : guard.Reason;
            metrics.GuardBlock("document", reason);
            logger.LogWarning("Policy document {DocumentId} version {Version} was flagged by the document guard", job.DocumentId, job.Version);
            await EndAsync(job, PolicyDocumentStatus.Flagged, reason, cancellationToken);
            return;
        }

        var plan = planner.Plan(extracted);
        var children = plan.Where(c => c.Kind == ChunkKind.Child).ToList();
        if (children.Count == 0)
        {
            throw new TextExtractionException("The file has no text to index.");
        }

        var stored = await PrepareAsync(job, plan, cancellationToken);
        if (stored is null)
        {
            return;
        }

        var todo = children.Where(c => !stored.ContainsKey(c.Ordinal)).ToList();
        var done = children.Count - todo.Count;
        var model = ai.Value.EffectiveEmbeddingModel;
        IReadOnlyDictionary<string, ReusableEmbedding> reusable;
        await using (var unit = await units.BeginAsync(cancellationToken))
        {
            reusable = await EmbeddingReuse.FindAsync(
                unit.Db, job.TenantId, [.. todo.Select(c => c.ContentHash).Distinct(StringComparer.Ordinal)], model, cancellationToken);
        }

        var step = job.ProgressStep;
        foreach (var batch in todo.Chunk(options.Value.Ingestion.BatchSize))
        {
            var chunks = await EmbedAsync(job, batch, stored, reusable, model, cancellationToken);
            done += batch.Length;
            var percent = done * 100 / children.Count;
            int? newStep = percent / 10 * 10 > step ? percent / 10 * 10 : null;
            var current = await ChangeAsync(job.DocumentId, job.Version, async (unit, document) =>
            {
                unit.Db.Chunks.AddRange(chunks);
                document.SetProgress(job.Version, percent, time.GetUtcNow());
                if (newStep is { } reached)
                {
                    await unit.PublishAsync(new PolicyDocumentProgressed(job.DocumentId, job.TenantId, job.Version, reached, time.GetUtcNow()));
                }
            }, cancellationToken);
            if (!current)
            {
                return;
            }

            step = newStep ?? step;
        }

        await ChangeAsync(job.DocumentId, job.Version, async (unit, document) =>
        {
            if (step < 100)
            {
                // Everything was stored by an earlier attempt: the last step is still reported.
                await unit.PublishAsync(new PolicyDocumentProgressed(job.DocumentId, job.TenantId, job.Version, 100, time.GetUtcNow()));
            }

            // The switch: older versions' chunks go and this version becomes current, in one transaction.
            await unit.Db.Chunks.IgnoreQueryFilters()
                .Where(c => c.DocumentId == job.DocumentId && c.DocumentVersion != job.Version)
                .ExecuteDeleteAsync(cancellationToken);
            document.MarkReady(job.Version, children.Count, time.GetUtcNow());
            await PublishStatusAsync(unit, document, job.Version);
        }, cancellationToken);
        logger.LogInformation(
            "Policy document {DocumentId} version {Version} is ready: {Chunks} chunks, {Embedded} embedded now",
            job.DocumentId, job.Version, children.Count, todo.Count);
    }

    /// <summary>
    /// Stores the parents (and keeps what an earlier attempt stored), moves the version to <c>Embedding</c>. Returns the stored chunks
    /// of the version by ordinal (their IDs, so children can name their parent), or null when the version is stale.
    /// </summary>
    private async Task<Dictionary<int, Guid>?> PrepareAsync(Job job, IReadOnlyList<PlannedChunk> plan, CancellationToken cancellationToken)
    {
        Dictionary<int, Guid>? stored = null;
        var current = await ChangeAsync(job.DocumentId, job.Version, async (unit, document) =>
        {
            var existing = await unit.Db.Chunks.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.DocumentId == job.DocumentId && c.DocumentVersion == job.Version)
                .Select(c => new { c.Id, c.Ordinal, c.Kind, c.ContentHash })
                .ToListAsync(cancellationToken);
            var planned = plan.ToDictionary(c => c.Ordinal);
            if (existing.Any(e => !planned.TryGetValue(e.Ordinal, out var p) || p.Kind != e.Kind || p.ContentHash != e.ContentHash))
            {
                // Stored by an attempt with another plan (the chunking settings changed): start this version again.
                await DeleteVersionAsync(unit, job.DocumentId, job.Version, cancellationToken);
                existing.Clear();
            }

            stored = existing.ToDictionary(e => e.Ordinal, e => e.Id);
            var now = time.GetUtcNow();
            foreach (var parent in plan.Where(c => c.Kind == ChunkKind.Parent && !stored.ContainsKey(c.Ordinal)))
            {
                var chunk = NewChunk(job, parent, null, now);
                unit.Db.Chunks.Add(chunk);
                stored[parent.Ordinal] = chunk.Id;
            }

            if (document.Status == PolicyDocumentStatus.Checking && document.StartEmbedding(job.Version, now))
            {
                await PublishStatusAsync(unit, document, job.Version);
            }
        }, cancellationToken);
        return current ? stored : null;
    }

    private async Task<List<Chunk>> EmbedAsync(
        Job job, PlannedChunk[] batch, Dictionary<int, Guid> stored, IReadOnlyDictionary<string, ReusableEmbedding> reusable, string model,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var chunks = new List<Chunk>(batch.Length);
        var toEmbed = new List<(Chunk Chunk, string? Context, string Input)>();
        foreach (var planned in batch)
        {
            var parentId = planned.ParentOrdinal is { } parent && stored.TryGetValue(parent, out var id) ? id : (Guid?)null;
            var chunk = NewChunk(job, planned, parentId, now);
            chunks.Add(chunk);
            if (reusable.TryGetValue(planned.ContentHash, out var reuse))
            {
                chunk.SetEmbedding(model, reuse.Embedding, reuse.Context);
                continue;
            }

            var context = options.Value.Ingestion.ContextualRetrieval
                ? await contextWriter.WriteAsync(job.Title, planned, cancellationToken)
                : null;
            toEmbed.Add((chunk, context, context is null ? planned.Text : context + "\n" + planned.Text));
        }

        if (toEmbed.Count > 0)
        {
            var generated = await embeddings.GenerateAsync([.. toEmbed.Select(e => e.Input)], cancellationToken: cancellationToken);
            metrics.EmbeddingUsage(model, job.TenantId, generated.Usage?.InputTokenCount ?? 0, ai.Value.EffectivePrices);
            for (var i = 0; i < toEmbed.Count; i++)
            {
                toEmbed[i].Chunk.SetEmbedding(model, generated[i].Vector.ToArray(), toEmbed[i].Context);
            }
        }

        return chunks;
    }

    /// <summary>Ends the version as <c>Failed</c> or <c>Flagged</c>: what it stored is deleted, the current version stays searchable.</summary>
    private Task EndAsync(Job job, PolicyDocumentStatus status, string reason, CancellationToken cancellationToken) =>
        ChangeAsync(job.DocumentId, job.Version, (unit, document) => EndInAsync(unit, document, job.Version, status, reason, cancellationToken), cancellationToken);

    private async Task EndInAsync(
        IAssistantUnitOfWork unit, PolicyDocument document, int version, PolicyDocumentStatus status, string reason, CancellationToken cancellationToken)
    {
        await DeleteVersionAsync(unit, document.Id, version, cancellationToken);
        var now = time.GetUtcNow();
        var ended = status == PolicyDocumentStatus.Flagged ? document.Flag(version, reason, now) : document.Fail(version, reason, now);
        if (ended)
        {
            await PublishStatusAsync(unit, document, version);
        }
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the document in a unit of work and commits it, again (from a fresh load) when it lost a race on
    /// the document row. When the version is no longer the newest, it only deletes what the version stored and returns false.
    /// </summary>
    private async Task<bool> ChangeAsync(
        Guid documentId, int version, Func<IAssistantUnitOfWork, PolicyDocument, Task> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var unit = await units.BeginAsync(cancellationToken);
            var document = await unit.Db.PolicyDocuments.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
            if (document is null)
            {
                return false;
            }

            var current = document.IsNewest(version);
            if (current)
            {
                await change(unit, document);
            }
            else if (document.CurrentVersion != version)
            {
                await DeleteVersionAsync(unit, documentId, version, cancellationToken);
            }

            try
            {
                await unit.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < CommitAttempts)
            {
                continue;
            }

            await EvictAsync(document);
            return current;
        }
    }

    private static Task<int> DeleteVersionAsync(IAssistantUnitOfWork unit, Guid documentId, int version, CancellationToken cancellationToken) =>
        unit.Db.Chunks.IgnoreQueryFilters()
            .Where(c => c.DocumentId == documentId && c.DocumentVersion == version)
            .ExecuteDeleteAsync(cancellationToken);

    private ValueTask PublishStatusAsync(IAssistantUnitOfWork unit, PolicyDocument document, int version) =>
        unit.PublishAsync(new PolicyDocumentStatusChanged(document.Id, document.TenantId, version, document.Status.ToString(), time.GetUtcNow()));

    private static Chunk NewChunk(Job job, PlannedChunk planned, Guid? parentId, DateTimeOffset now) => new(
        Guid.CreateVersion7(), job.TenantId, SourceType.Policy, planned.Kind, parentId, planned.Ordinal, SectionPath(planned.SectionPath),
        planned.Text, planned.ContentHash, now, job.DocumentId, job.Version);

    private static string SectionPath(IReadOnlyList<string> path)
    {
        var text = string.Join(SectionSeparator, path);
        return text.Length <= Chunk.SectionMaxLength ? text : text[..(Chunk.SectionMaxLength - 1)] + "…";
    }

    /// <summary>The cached reads of the document and the tenant's list; a failure is logged, the entries expire within a second anyway.</summary>
    private async Task EvictAsync(PolicyDocument document)
    {
        try
        {
            await cache.RemoveByTagAsync(AssistantCacheTag.Of(document.TenantId, document.Id), CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache invalidation failed for policy document {DocumentId}", document.Id);
        }
    }
}
