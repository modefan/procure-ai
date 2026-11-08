using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Application.Documents;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Application.Telemetry;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
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
/// stored. A model call (guard, context, embeddings) that failed for a reason that may pass is rethrown as
/// <see cref="AiProviderUnavailableException"/> (scheduled retries,
/// <c>Assistant:Ingestion:RetryDelays</c>); on the last attempt the version is <c>Failed</c> with the reason. A passing database failure
/// is left to the shared scheduled retry; anything else (a bug) ends the version <c>Failed</c> ("Ingestion failed unexpectedly.") instead
/// of dead-lettering the message. A reused chunk copies the context and vector of the earlier chunk of the tenant with the same content
/// hash and embedding model (<see cref="EmbeddingReuse"/>).
/// </summary>
public sealed class PolicyIngestion(
    IAssistantUnitOfWorkFactory units,
    IEnumerable<ITextExtractor> extractors,
    ChunkPlanner planner,
    IPromptShield shield,
    ContextWriter contextWriter,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IAiFailures failures,
    IDatabaseFailures databaseFailures,
    ITenantTokenBudget budget,
    ICacheInvalidator cache,
    AssistantMetrics metrics,
    IOptions<AiOptions> ai,
    IOptions<AssistantOptions> options,
    TimeProvider time,
    ILogger<PolicyIngestion> logger)
{
    /// <summary>How often a commit that lost a race on the document row (a replace or remove meanwhile) is tried again.</summary>
    private const int CommitAttempts = 3;

    /// <summary>
    /// The demo seed's own document: the first version, uploaded by <see cref="SystemCaller"/> (<c>DemoSeeder</c>). Its ingestion is counted
    /// against the tenant's daily token total but never stopped by the cap. Any later version is a user's replacement (a replace records
    /// the user as the uploader), and requiring version 1 keeps a future internal upload without an on-behalf-of user from slipping past
    /// the cap through a replace.
    /// </summary>
    public static bool IsDemoSeed(string uploadedBy, int version) => uploadedBy == SystemCaller.Id && version == 1;

    /// <param name="Seeded">The demo seed's own document (<see cref="IsDemoSeed"/>): counted, but never stopped by the cap.</param>
    private sealed record Job(
        Guid DocumentId, int Version, string TenantId, string Title, string ContentType, byte[] Content, int ProgressStep, bool Seeded);

    /// <param name="attempt">The delivery attempt, 1 for the first (Wolverine's <see cref="Envelope.Attempts"/>).</param>
    public async Task RunAsync(IngestPolicyDocument message, int attempt, CancellationToken cancellationToken)
    {
        var job = await StartAsync(message, cancellationToken);
        if (job is null)
        {
            return;
        }

        // Every model call of the job counts against the document's tenant's daily token total, and a tenant that reached its cap
        // gets no more: checked here (a queued job and every retry pass this point) and before each batch of chunks. The demo seed's
        // own document is the exception (counted, never stopped): the nightly reset deletes the policy and seeds it again, and a tenant
        // capped between midnight and the reset would otherwise have no policy for the whole day. It is one fixed document a night.
        using var usage = AiUsageScope.For(job.TenantId);
        try
        {
            await StopIfBudgetSpentAsync(job, cancellationToken);
            await IngestAsync(job, cancellationToken);
        }
        catch (BudgetReachedException)
        {
            logger.LogWarning(
                "Policy document {DocumentId} version {Version} stopped: tenant {TenantId} reached its daily AI token limit",
                job.DocumentId, job.Version, job.TenantId);
            await EndAsync(job, PolicyDocumentStatus.Failed, BudgetReachedReason, cancellationToken);
        }
        catch (GuardBlockedException blocked) when (blocked.Reason == GuardReasons.ProviderFilter)
        {
            // The provider's content filter refused a chunk context or embedding call (our document guard had passed it).
            metrics.GuardBlock("document", blocked.Reason);
            logger.LogWarning(
                "Policy document {DocumentId} version {Version} was refused by the model provider's content filter", job.DocumentId, job.Version);
            await EndAsync(job, PolicyDocumentStatus.Flagged, GuardReasons.Describe(blocked.Reason), cancellationToken);
        }
        catch (TextExtractionException exception)
        {
            logger.LogWarning(exception, "Policy document {DocumentId} version {Version} could not be read", job.DocumentId, job.Version);
            await EndAsync(job, PolicyDocumentStatus.Failed, exception.Message, cancellationToken);
        }
        catch (AiProviderUnavailableException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var retries = options.Value.Ingestion.RetryDelaySchedule().Length;
            if (attempt <= retries)
            {
                logger.LogWarning(
                    exception.InnerException, "The AI model failed for policy document {DocumentId} version {Version}, attempt {Attempt}; retrying",
                    job.DocumentId, job.Version, attempt);
                throw;
            }

            logger.LogError(
                exception.InnerException, "The AI model failed for policy document {DocumentId} version {Version} after {Attempts} attempts; giving up",
                job.DocumentId, job.Version, attempt);
            await EndAsync(
                job, PolicyDocumentStatus.Failed,
                $"The AI model could not be reached ({exception.InnerException?.GetType().Name}) after {attempt} attempts. Upload the file again later.",
                cancellationToken);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested && exception is not OperationCanceledException
            && !databaseFailures.IsTransientDatabaseFailure(exception))
        {
            // A bug or a state nothing retries (a lost race after its retries, a domain rule): without this the message would be
            // dead-lettered and the version left checking or embedding for good. A passing database failure is not caught, so the
            // shared scheduled retry still applies. Only IDs are logged with the exception.
            logger.LogError(
                exception, "Ingestion of policy document {DocumentId} version {Version} failed unexpectedly", job.DocumentId, job.Version);
            await EndAsync(job, PolicyDocumentStatus.Failed, UnexpectedFailure, cancellationToken);
        }
    }

    /// <summary>The reason shown for a version stopped by the tenant's daily token cap (<c>AI:DailyTokenCapPerTenant</c>).</summary>
    public const string BudgetReachedReason = "Your organisation reached today's AI limit; upload the file again tomorrow.";

    /// <summary>The reason shown for a version whose ingestion failed for an unexpected reason (details are in the log only).</summary>
    public const string UnexpectedFailure = "Ingestion failed unexpectedly.";

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

            job = new Job(
                document.Id, message.Version, document.TenantId, document.Title, file.ContentType, file.Content, document.Progress / 10 * 10,
                IsDemoSeed(document.UploadedBy, message.Version));
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

        // The guard reads everything of the document that reaches a prompt: the title (typed by the uploader), the headings (stored as
        // the section paths) and the text.
        var guardInput = string.Join("\n\n", [job.Title, .. extracted.Sections.Select(s => string.Join(" > ", s.Path) + "\n" + s.Text)]);
        var guard = await ModelCallAsync(() => shield.CheckDocumentAsync(guardInput, cancellationToken), cancellationToken);
        if (guard.Blocked)
        {
            // The metric gets the reason code, the document the sentence for people.
            var code = string.IsNullOrWhiteSpace(guard.Reason) ? GuardReasons.Injection : guard.Reason;
            var reason = GuardReasons.Describe(code);
            metrics.GuardBlock("document", code);
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
            await StopIfBudgetSpentAsync(job, cancellationToken);
            var chunks = await EmbedAsync(job, batch, stored, reusable, model, cancellationToken);
            done += batch.Length;
            var percent = done * 100 / children.Count;
            int? newStep = percent / 10 * 10 > step ? percent / 10 * 10 : null;
            var current = await ChangeAsync(job.DocumentId, job.Version, async (unit, document) =>
            {
                unit.Db.Chunks.AddRange(chunks);
                // The document row is written only when a new 10 % step is reached (one audit row, one CDC change and one new ETag per
                // step, not per batch). A retry resumes from the stored chunks, not from this number.
                if (newStep is { } reached)
                {
                    document.SetProgress(job.Version, percent, time.GetUtcNow());
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
                ? await ModelCallAsync(() => contextWriter.WriteAsync(job.Title, planned, cancellationToken), cancellationToken)
                : null;
            toEmbed.Add((chunk, context, context is null ? planned.Text : context + "\n" + planned.Text));
        }

        if (toEmbed.Count > 0)
        {
            var generated = await ModelCallAsync(
                () => embeddings.GenerateAsync([.. toEmbed.Select(e => e.Input)], cancellationToken: cancellationToken), cancellationToken);
            metrics.EmbeddingUsage(model, job.TenantId, generated.Usage?.InputTokenCount ?? 0, ai.Value.EffectivePrices);
            for (var i = 0; i < toEmbed.Count; i++)
            {
                toEmbed[i].Chunk.SetEmbedding(model, generated[i].Vector.ToArray(), toEmbed[i].Context);
            }
        }

        return chunks;
    }

    /// <summary>
    /// Throws <see cref="BudgetReachedException"/> when the document's tenant reached its daily token cap (never for the demo seed's own
    /// document, <see cref="Job.Seeded"/>). When Redis cannot be read the job goes on (logged): the counting stage undercounts in that
    /// case too, and a Redis outage should not fail every document.
    /// </summary>
    private async Task StopIfBudgetSpentAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.Seeded)
        {
            return;
        }

        TokenBudget today;
        try
        {
            today = await budget.GetAsync(job.TenantId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The daily token total of tenant {TenantId} could not be read; ingestion goes on", job.TenantId);
            return;
        }

        if (today.Spent)
        {
            throw new BudgetReachedException();
        }
    }

    /// <summary>The tenant's daily token cap is reached: the version ends <c>Failed</c> with <see cref="BudgetReachedReason"/>, never retried.</summary>
    private sealed class BudgetReachedException() : Exception("The tenant's daily AI token limit is reached.");

    /// <summary>
    /// Runs a call to a model (the guard, the context writer, the embeddings); a failure of it that may pass becomes an
    /// <see cref="AiProviderUnavailableException"/>. Only these calls are classified, so a database timeout is never reported as the model.
    /// </summary>
    private async Task<T> ModelCallAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && failures.IsTransient(exception))
        {
            throw new AiProviderUnavailableException(exception);
        }
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
        var text = string.Join(Chunk.SectionSeparator, path);
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
