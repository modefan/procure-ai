using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using Pgvector;
using ProcureAi.Assistant.Domain;
using ProcureAi.BuildingBlocks.Domain;
using ProcureAi.BuildingBlocks.Persistence;

namespace ProcureAi.Assistant.Infrastructure.Persistence.Configurations;

/// <summary>Column sizes of the Assistant's tables.</summary>
public static class AssistantColumns
{
    public const int TenantLength = 100;
    public const int UserLength = 100;
    public const string Money = "numeric(18,2)";

    /// <summary>The embedding column: <c>vector(1536)</c> (ruling 2).</summary>
    public static readonly string Vector = $"vector({Chunk.EmbeddingDimensions})";

    /// <summary>The generated full-text column of <c>chunks</c> (a shadow property: the Domain never sees it).</summary>
    public const string SearchProperty = "Search";

    public const string SearchSql = "to_tsvector('english', coalesce(context,'') || ' ' || text)";
}

internal sealed class PolicyDocumentConfiguration : IEntityTypeConfiguration<PolicyDocument>
{
    public void Configure(EntityTypeBuilder<PolicyDocument> builder)
    {
        builder.ToTable("policy_documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.TenantId).HasColumnName("tenant_id").HasMaxLength(AssistantColumns.TenantLength).IsRequired();
        builder.Property(d => d.Title).HasColumnName("title").HasMaxLength(PolicyDocument.TitleMaxLength).IsRequired();
        builder.Property(d => d.FileName).HasColumnName("file_name").HasMaxLength(PolicyDocument.FileNameMaxLength).IsRequired();
        builder.Property(d => d.Version).HasColumnName("version");
        builder.Property(d => d.CurrentVersion).HasColumnName("current_version");
        builder.Property(d => d.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Progress).HasColumnName("progress");
        builder.Property(d => d.ChunkCount).HasColumnName("chunk_count");
        builder.Property(d => d.FlaggedReason).HasColumnName("flagged_reason").HasMaxLength(PolicyDocument.ReasonMaxLength);
        builder.Property(d => d.FailureReason).HasColumnName("failure_reason").HasMaxLength(PolicyDocument.ReasonMaxLength);
        builder.Property(d => d.Removed).HasColumnName("removed");
        builder.Property(d => d.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(AssistantColumns.UserLength).IsRequired();
        builder.Property(d => d.UploadedAt).HasColumnName("uploaded_at");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        // xmin changes on every write of the row: the optimistic concurrency token (If-Match).
        builder.Property(d => d.RowVersion).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        builder.Ignore(d => d.DomainEvents);
        builder.HasIndex(d => new { d.TenantId, d.Removed });
    }
}

internal sealed class PolicyDocumentFileConfiguration : IEntityTypeConfiguration<PolicyDocumentFile>
{
    public void Configure(EntityTypeBuilder<PolicyDocumentFile> builder)
    {
        builder.ToTable("policy_document_files");
        // File bytes are not business changes: no audit row (and not captured by Debezium).
        builder.ExcludeFromAudit();
        builder.HasKey(f => new { f.DocumentId, f.Version });
        builder.Property(f => f.DocumentId).HasColumnName("document_id");
        builder.Property(f => f.Version).HasColumnName("version").ValueGeneratedNever();
        builder.Property(f => f.TenantId).HasColumnName("tenant_id").HasMaxLength(AssistantColumns.TenantLength).IsRequired();
        builder.Property(f => f.ContentType).HasColumnName("content_type").HasMaxLength(PolicyDocumentFile.ContentTypeMaxLength).IsRequired();
        builder.Property(f => f.Content).HasColumnName("content").HasColumnType("bytea").IsRequired();
        builder.Property(f => f.SizeBytes).HasColumnName("size_bytes");
        builder.Property(f => f.Sha256).HasColumnName("sha256").HasMaxLength(Chunk.HashLength).IsRequired();
        builder.Property(f => f.UploadedAt).HasColumnName("uploaded_at");
        builder.HasOne<PolicyDocument>().WithMany().HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
    public const string EmbeddingIndex = "ix_chunks_embedding_hnsw";
    public const string SearchIndex = "ix_chunks_search_gin";

    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable("chunks");
        // Chunks and their embeddings are derived data (re-made from the files): no audit row, not captured by Debezium.
        builder.ExcludeFromAudit();
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id").HasMaxLength(AssistantColumns.TenantLength).IsRequired();
        builder.Property(c => c.SourceType).HasColumnName("source_type").HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.ParentId).HasColumnName("parent_id");
        builder.Property(c => c.DocumentId).HasColumnName("document_id");
        builder.Property(c => c.DocumentVersion).HasColumnName("document_version");
        builder.Property(c => c.PurchaseRequestId).HasColumnName("purchase_request_id");
        builder.Property(c => c.Ordinal).HasColumnName("ordinal");
        builder.Property(c => c.SectionPath).HasColumnName("section_path").HasMaxLength(Chunk.SectionMaxLength).IsRequired();
        builder.Property(c => c.Text).HasColumnName("text").IsRequired();
        builder.Property(c => c.Context).HasColumnName("context");
        builder.Property(c => c.TokenCount).HasColumnName("token_count");
        builder.Property(c => c.ContentHash).HasColumnName("content_hash").HasMaxLength(Chunk.HashLength).IsRequired();
        builder.Property(c => c.EmbeddingModel).HasColumnName("embedding_model").HasMaxLength(Chunk.ModelMaxLength);
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");

        // The Domain keeps a plain float[]; the column is pgvector's vector(1536).
        builder.Property(c => c.Embedding).HasColumnName("embedding").HasColumnType(AssistantColumns.Vector)
            .HasConversion(
                floats => floats == null ? null : new Vector(floats),
                vector => vector == null ? null : vector.ToArray(),
                new ValueComparer<float[]?>(
                    (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
                    v => v == null ? 0 : v.Length,
                    v => v == null ? null : v.ToArray()));
        // Approximate nearest neighbours by cosine distance (the <=> operator).
        builder.HasIndex(c => c.Embedding).HasDatabaseName(EmbeddingIndex).HasMethod("hnsw").HasOperators("vector_cosine_ops");

        // Keyword search: a stored generated tsvector of context and text, GIN-indexed.
        builder.Property<NpgsqlTsVector>(AssistantColumns.SearchProperty).HasColumnName("search")
            .HasComputedColumnSql(AssistantColumns.SearchSql, stored: true);
        builder.HasIndex(AssistantColumns.SearchProperty).HasDatabaseName(SearchIndex).HasMethod("gin");

        builder.HasIndex(c => new { c.TenantId, c.DocumentId, c.DocumentVersion });
        builder.HasIndex(c => new { c.TenantId, c.ContentHash, c.EmbeddingModel });
        builder.HasIndex(c => new { c.TenantId, c.PurchaseRequestId });
        builder.HasOne<Chunk>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseConfiguration : IEntityTypeConfiguration<Purchase>
{
    public void Configure(EntityTypeBuilder<Purchase> builder)
    {
        builder.ToTable("purchases");
        builder.HasKey(p => p.RequestId);
        builder.Property(p => p.RequestId).HasColumnName("request_id").ValueGeneratedNever();
        builder.Property(p => p.TenantId).HasColumnName("tenant_id").HasMaxLength(AssistantColumns.TenantLength).IsRequired();
        builder.Property(p => p.Number).HasColumnName("number").HasMaxLength(Purchase.NumberMaxLength).IsRequired();
        builder.Property(p => p.Title).HasColumnName("title").HasMaxLength(Purchase.TitleMaxLength).IsRequired();
        builder.Property(p => p.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(Purchase.CodeMaxLength).IsRequired();
        builder.Property(p => p.TotalInclVat).HasColumnName("total_incl_vat").HasColumnType(AssistantColumns.Money)
            .HasConversion(money => money.Amount, amount => Money.Zar(amount));
        builder.Property(p => p.SupplierCode).HasColumnName("supplier_code").HasMaxLength(Purchase.CodeMaxLength);
        builder.Property(p => p.LinesSummary).HasColumnName("lines_summary").IsRequired();
        builder.Property(p => p.ApproverIds).HasColumnName("approver_ids").IsRequired();
        builder.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(p => p.ApprovedAt).HasColumnName("approved_at");
        builder.Property(p => p.CompletedAt).HasColumnName("completed_at");
        // xmin changes on every write of the row: two events of one request handled at once cannot overwrite each other.
        builder.Property(p => p.RowVersion).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        builder.Ignore(p => p.HasSubmitData);
        builder.Ignore(p => p.IsIndexable);
        builder.Ignore(p => p.ApproverIdList);
        builder.HasIndex(p => new { p.TenantId, p.Status });
    }
}
