using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>Changes to this entity are not audited (technical tables).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditExcludeAttribute : Attribute;

/// <summary>
/// The same as <see cref="AuditExcludeAttribute"/>, set in the EF Core model instead of on the class, for an entity whose class lives in a
/// Domain project (which may not reference this library), e.g. the Assistant's chunks and uploaded file bytes.
/// </summary>
public static class AuditExclusion
{
    /// <summary>The model annotation <see cref="ExcludeFromAudit"/> sets.</summary>
    public const string Annotation = "ProcureAi:AuditExclude";

    /// <summary>Changes to this entity are not audited (technical tables).</summary>
    public static EntityTypeBuilder<T> ExcludeFromAudit<T>(this EntityTypeBuilder<T> builder)
        where T : class
    {
        builder.HasAnnotation(Annotation, true);
        return builder;
    }
}
