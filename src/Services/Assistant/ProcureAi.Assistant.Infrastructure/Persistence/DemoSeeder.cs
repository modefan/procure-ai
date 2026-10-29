using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcureAi.Assistant.Application.Ingestion;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Application;
using Wolverine;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace ProcureAi.Assistant.Infrastructure.Persistence;

/// <summary>
/// The demo data the AppHost migrate step writes when <c>DemoSeed:Enabled</c> is true (plan 05 ruling 17): the procurement policy
/// (<c>infra/seed/procurement-policy.md</c>, embedded in this assembly) for the tenants <c>acme</c> and <c>globex</c>, with its ingestion
/// queued. The seed runs in <c>--migrate</c>, before Debezium and without the model provider: it inserts the document, its file and a
/// durable <see cref="IngestPolicyDocument"/> for the local queue in one transaction, straight into Wolverine's inbox table (the
/// migrate process never starts Wolverine). The running api's durability agent recovers that message (owner "any node") and ingests the
/// document. Idempotent: a document that exists (by its fixed ID) is left alone.
/// </summary>
public static class DemoSeeder
{
    public const string Title = "Procurement policy";
    public const string FileName = "procurement-policy.md";
    private const string Resource = "ProcureAi.Assistant.Infrastructure.Seed.procurement-policy.md";

    /// <summary>The seeded documents: fixed IDs, so a second run adds nothing.</summary>
    public static IReadOnlyList<(string TenantId, Guid DocumentId)> Documents { get; } =
    [
        ("acme", Guid.Parse("5eed0000-0000-4000-8000-00000000a001")),
        ("globex", Guid.Parse("5eed0000-0000-4000-8000-00000000a002")),
    ];

    public static async Task SeedDemoDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var content = await ReadPolicyAsync(cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));

        // The seed has no caller, so the tenant filter matches nothing: look the documents up by ID.
        var ids = Documents.Select(d => d.DocumentId).ToArray();
        var existing = await db.PolicyDocuments.IgnoreQueryFilters().Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken);
        foreach (var (tenant, id) in Documents.Where(d => !existing.Contains(d.DocumentId)))
        {
            var document = new PolicyDocument(id, tenant, Title, FileName, SystemCaller.Id, now);
            db.PolicyDocuments.Add(document);
            db.PolicyDocumentFiles.Add(new PolicyDocumentFile(id, document.Version, tenant, PolicyFileType.Markdown, content, sha256, now));
            db.Add(new IncomingMessage(IngestionEnvelope(runtime, new IngestPolicyDocument(id, document.Version))));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The policy text as it is seeded.</summary>
    public static async Task<byte[]> ReadPolicyAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(DemoSeeder).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The embedded seed {Resource} is missing.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// The message as Wolverine stores a durable local message that no node owns yet: status Incoming, owner 0 (any node), the local
    /// queue's address as its destination, the body written by the default (JSON) serializer.
    /// </summary>
    private static Envelope IngestionEnvelope(IWolverineRuntime runtime, IngestPolicyDocument message) =>
        new(message)
        {
            Id = Guid.CreateVersion7(),
            Destination = new Uri($"local://{DependencyInjection.IngestionQueue}"),
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            ContentType = runtime.Options.DefaultSerializer.ContentType,
            Serializer = runtime.Options.DefaultSerializer,
        };
}
