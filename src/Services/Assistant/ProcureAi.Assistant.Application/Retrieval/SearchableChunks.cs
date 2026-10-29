using ProcureAi.Assistant.Application.Abstractions;
using ProcureAi.Assistant.Domain;

namespace ProcureAi.Assistant.Application.Retrieval;

/// <summary>
/// The chunks retrieval may return (ledger ruling B): every purchase chunk, and the policy chunks of the <b>current</b> version of a
/// document that was not removed. A version being embedded, a flagged or failed one, a replaced one and a removed document are never
/// searched. The tenant filter of the context applies on top.
/// </summary>
public static class SearchableChunks
{
    public static IQueryable<Chunk> Of(IAssistantDbContext db) =>
        db.Chunks.Where(c => c.SourceType != SourceType.Policy
            || db.PolicyDocuments.Any(d => d.Id == c.DocumentId && !d.Removed && d.CurrentVersion == c.DocumentVersion));
}
