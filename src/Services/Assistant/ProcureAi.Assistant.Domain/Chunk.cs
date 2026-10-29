using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Domain;

/// <summary>What a chunk was cut from.</summary>
public enum SourceType
{
    /// <summary>A section of a policy document.</summary>
    Policy,

    /// <summary>A completed purchase request (ruling 9).</summary>
    Purchase,
}

/// <summary>Parent–child chunking: children are embedded and searched, a hit brings its parent section's text to the prompt.</summary>
public enum ChunkKind
{
    Parent,
    Child,
}

/// <summary>
/// A piece of searchable text of one tenant, with its embedding (<see cref="EmbeddingDimensions"/> floats, ruling 2) and the model
/// that made it. Postgres keeps a full-text vector of <c>context + text</c> next to it (a generated column), so retrieval can combine
/// vector and keyword search. Technical storage: not audited row by row and not captured by Debezium.
/// </summary>
public sealed class Chunk
{
    /// <summary>The size of every embedding (ruling 2): the <c>vector(1536)</c> column. Changing it needs a migration and re-embedding.</summary>
    public const int EmbeddingDimensions = 1536;

    public const int SectionMaxLength = 500;
    public const int ModelMaxLength = 100;
    public const int HashLength = 64;

    private Chunk()
    {
    }

    public Chunk(
        Guid id, string tenantId, SourceType sourceType, ChunkKind kind, Guid? parentId, int ordinal, string sectionPath, string text,
        string contentHash, DateTimeOffset createdAt, Guid? documentId = null, int? documentVersion = null, Guid? purchaseRequestId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new DomainException("A chunk needs a tenant.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DomainException("A chunk needs text.");
        }

        if (sourceType == SourceType.Policy && (documentId is null || documentVersion is null))
        {
            throw new DomainException("A policy chunk needs its document and version.");
        }

        if (sourceType == SourceType.Purchase && purchaseRequestId is null)
        {
            throw new DomainException("A purchase chunk needs its request.");
        }

        Id = id;
        TenantId = tenantId;
        SourceType = sourceType;
        Kind = kind;
        ParentId = parentId;
        Ordinal = ordinal;
        SectionPath = sectionPath;
        Text = text;
        ContentHash = contentHash;
        CreatedAt = createdAt;
        DocumentId = documentId;
        DocumentVersion = documentVersion;
        PurchaseRequestId = purchaseRequestId;
        TokenCount = (int)Math.Ceiling(text.Length / 4.0);
    }

    public Guid Id { get; private set; }

    public string TenantId { get; private set; } = "";

    public SourceType SourceType { get; private set; }

    public ChunkKind Kind { get; private set; }

    public Guid? ParentId { get; private set; }

    public Guid? DocumentId { get; private set; }

    public int? DocumentVersion { get; private set; }

    public Guid? PurchaseRequestId { get; private set; }

    public int Ordinal { get; private set; }

    /// <summary>Where in the document the text is, e.g. "3 Approvals › 3.2 Thresholds".</summary>
    public string SectionPath { get; private set; } = "";

    public string Text { get; private set; } = "";

    /// <summary>One or two sentences that situate the chunk in its document (contextual retrieval); null when not written.</summary>
    public string? Context { get; private set; }

    /// <summary>Estimated as <c>ceil(chars / 4)</c> (ruling 5).</summary>
    public int TokenCount { get; private set; }

    /// <summary>Hex SHA-256 of the text, so an unchanged chunk reuses its context and embedding.</summary>
    public string ContentHash { get; private set; } = "";

    public string? EmbeddingModel { get; private set; }

    public float[]? Embedding { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Stores the embedding and the model that made it; it must have <see cref="EmbeddingDimensions"/> values.</summary>
    public void SetEmbedding(string model, float[] embedding, string? context)
    {
        if (embedding.Length != EmbeddingDimensions)
        {
            throw new DomainException($"An embedding has {EmbeddingDimensions} values, not {embedding.Length}.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new DomainException("An embedding needs the name of its model.");
        }

        EmbeddingModel = model;
        Embedding = embedding;
        Context = context;
    }
}
