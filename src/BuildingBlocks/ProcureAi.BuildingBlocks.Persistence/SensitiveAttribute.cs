namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>The property's old and new values are stored as <see cref="AuditSaveChangesInterceptor.MaskedValue"/> in the audit.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute;
