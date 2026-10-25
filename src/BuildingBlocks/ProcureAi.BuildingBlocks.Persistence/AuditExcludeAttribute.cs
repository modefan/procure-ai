namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>Changes to this entity are not audited (technical tables).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditExcludeAttribute : Attribute;
