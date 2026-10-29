using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Retrieval;
using ProcureAi.Assistant.Domain;
using ProcureAi.Assistant.Infrastructure.Ai.Fake;
using ProcureAi.Assistant.Infrastructure.Persistence;
using ProcureAi.Assistant.Infrastructure.Retrieval;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>A section of a seeded policy version: its headings, the parent's text and the children's texts.</summary>
public sealed record SeedSection(string[] Path, string ParentText, params string[] Children);

/// <summary>What <see cref="SearchSeed.AddDocumentAsync"/> stored: the document and the chunk IDs by text.</summary>
public sealed record SeededDocument(Guid Id, IReadOnlyDictionary<string, Guid> Children, IReadOnlyDictionary<string, Guid> Parents);

/// <summary>
/// Writes policy documents and their chunks straight into <c>assistantdb</c> (no ingestion), embedded as the Fake provider embeds them
/// (<see cref="FakeEmbeddingGenerator.Embed(string)"/>, no context), so the search tests control exactly what is searchable.
/// </summary>
internal static class SearchSeed
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A document of <paramref name="tenant"/> whose newest upload is <paramref name="newestVersion"/> and whose current (searchable) version
    /// is <paramref name="currentVersion"/> (0: none ready yet), with the chunks of each version in <paramref name="sections"/>.
    /// </summary>
    public static async Task<SeededDocument> AddDocumentAsync(
        AssistantApp app, string tenant, string title, int currentVersion, IReadOnlyList<(int Version, SeedSection Section)> sections,
        int? newestVersion = null, bool removed = false)
    {
        var now = DateTimeOffset.UtcNow;
        var document = new PolicyDocument(Guid.CreateVersion7(), tenant, title, "policy.md", "ada", now);
        var newest = newestVersion ?? Math.Max(currentVersion, 1);
        for (var version = 1; version <= newest; version++)
        {
            if (version > 1)
            {
                document.ReplaceFile("policy.md", "ada", now);
            }

            if (version <= currentVersion)
            {
                document.StartChecking(version, now);
                document.StartEmbedding(version, now);
                document.MarkReady(version, sections.Count(s => s.Version == version), now);
            }
        }

        if (removed)
        {
            document.Remove(now);
        }

        var children = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var parents = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using var db = NewDbContext(app);
        db.PolicyDocuments.Add(document);
        var ordinal = 0;
        foreach (var (version, section) in sections)
        {
            var path = string.Join(Chunk.SectionSeparator, section.Path);
            var parent = new Chunk(
                Guid.CreateVersion7(), tenant, SourceType.Policy, ChunkKind.Parent, null, ordinal++, path, section.ParentText, Hash(section.ParentText),
                now, document.Id, version);
            db.Chunks.Add(parent);
            parents[section.ParentText] = parent.Id;
            foreach (var text in section.Children)
            {
                var child = new Chunk(
                    Guid.CreateVersion7(), tenant, SourceType.Policy, ChunkKind.Child, parent.Id, ordinal++, path, text, Hash(text), now, document.Id,
                    version);
                child.SetEmbedding(AiOptions.FakeEmbeddingModel, FakeEmbeddingGenerator.Embed(text), null);
                db.Chunks.Add(child);
                children[text] = child.Id;
            }
        }

        await db.SaveChangesAsync(Ct);
        return new SeededDocument(document.Id, children, parents);
    }

    /// <summary>One ready document (version 1) with one section per text, each its own parent and child.</summary>
    public static Task<SeededDocument> AddTextsAsync(AssistantApp app, string tenant, params string[] texts) =>
        AddDocumentAsync(
            app, tenant, "Procurement policy", 1,
            [.. texts.Select((text, i) => (1, new SeedSection([$"{i + 1} Rule"], $"Rule {i + 1}. {text}", text)))]);

    /// <summary>
    /// The service's hybrid search as a user of the tenant of <paramref name="tenantDb"/>; <paramref name="candidatesPerArm"/> narrows the
    /// arms, <paramref name="embeddings"/> replaces the service's embedding generator for the query.
    /// </summary>
    public static PgHybridSearch Search(
        AssistantApp app, AssistantDbContext tenantDb, int? candidatesPerArm = null, int? topK = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        var configured = app.Services.GetRequiredService<IOptions<AssistantOptions>>().Value.Retrieval;
        var options = new AssistantOptions
        {
            Retrieval = new RetrievalOptions
            {
                TopK = topK ?? configured.TopK,
                CandidatesPerArm = candidatesPerArm ?? configured.CandidatesPerArm,
                RrfK = configured.RrfK,
                Rerank = configured.Rerank,
            },
        };
        object[] args = embeddings is null ? [tenantDb, Options.Create(options)] : [tenantDb, Options.Create(options), embeddings];
        return ActivatorUtilities.CreateInstance<PgHybridSearch>(app.Services, args);
    }

    private static AssistantDbContext NewDbContext(AssistantApp app) =>
        new(new DbContextOptionsBuilder<AssistantDbContext>().UseNpgsql(app.Infra.DbConnectionString, npgsql => npgsql.UseVector()).Options);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// A stand-in for a real embedding model in the tests: a query it knows (<paramref name="synonyms"/>) is embedded as the words it means,
/// anything else as itself, both with the Fake's lexical embedding. So a test can show the vector arm finding a chunk that shares no word
/// with the query.
/// </summary>
public sealed class SynonymEmbeddingGenerator(IReadOnlyDictionary<string, string> synonyms) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly FakeEmbeddingGenerator _inner = new();

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GenerateAsync(values.Select(v => synonyms.TryGetValue(v, out var meant) ? meant : v), options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
