using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Assistant.Domain;

/// <summary>
/// The uploaded bytes of one version of a policy document (ruling 12: stored in Postgres, a few MB for the demo). Technical
/// storage: neither audited row by row nor captured by Debezium (the document row is).
/// </summary>
public sealed class PolicyDocumentFile
{
    public const int ContentTypeMaxLength = 100;

    private PolicyDocumentFile()
    {
    }

    public PolicyDocumentFile(Guid documentId, int version, string tenantId, string contentType, byte[] content, string sha256, DateTimeOffset uploadedAt)
    {
        if (version < 1)
        {
            throw new DomainException("A file version must be 1 or more.");
        }

        if (content.Length == 0)
        {
            throw new DomainException("A policy document file cannot be empty.");
        }

        DocumentId = documentId;
        Version = version;
        TenantId = tenantId;
        ContentType = contentType;
        Content = content;
        SizeBytes = content.Length;
        Sha256 = sha256;
        UploadedAt = uploadedAt;
    }

    public Guid DocumentId { get; private set; }

    public int Version { get; private set; }

    public string TenantId { get; private set; } = "";

    public string ContentType { get; private set; } = "";

    public byte[] Content { get; private set; } = [];

    public long SizeBytes { get; private set; }

    /// <summary>Hex SHA-256 of <see cref="Content"/>.</summary>
    public string Sha256 { get; private set; } = "";

    public DateTimeOffset UploadedAt { get; private set; }
}
