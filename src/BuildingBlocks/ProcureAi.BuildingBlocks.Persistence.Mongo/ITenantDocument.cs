namespace ProcureAi.BuildingBlocks.Persistence.Mongo;

/// <summary>A document that belongs to one tenant. Every collection of tenant data is read and written through <see cref="TenantCollection{T}"/>.</summary>
public interface ITenantDocument
{
    /// <summary>The tenant the document belongs to. Must be a mapped (serialised) member of the document class.</summary>
    string TenantId { get; }
}
