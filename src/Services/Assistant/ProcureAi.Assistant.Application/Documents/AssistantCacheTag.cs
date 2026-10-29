using ProcureAi.BuildingBlocks.Application.Caching;

namespace ProcureAi.Assistant.Application.Documents;

/// <summary>The tags of the cached policy document reads: one document, and the tenant's list.</summary>
public static class AssistantCacheTag
{
    /// <summary>The route value is <c>id</c>.</summary>
    public const string DocumentTemplate = "assistant:{tenant}:policy-documents:{id}";

    public const string DocumentListTemplate = "assistant:{tenant}:policy-documents";

    /// <summary>Both tags a change of one document touches.</summary>
    public static string[] Of(string? tenantId, Guid documentId) =>
    [
        CacheTag.Format(DocumentTemplate, CacheTag.Tenant(tenantId), ("id", documentId))!,
        CacheTag.Format(DocumentListTemplate, CacheTag.Tenant(tenantId))!,
    ];
}
