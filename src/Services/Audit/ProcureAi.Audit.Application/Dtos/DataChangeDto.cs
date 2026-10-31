using ProcureAi.Audit.Domain;

namespace ProcureAi.Audit.Application.Dtos;

/// <summary>One data change with its field-level old and new values, and who made it. <c>TransactionId</c> is null for a MongoDB change.</summary>
public sealed record DataChangeDto(
    string Id,
    string Database,
    string Table,
    string PrimaryKey,
    Guid? SubjectId,
    string? TenantId,
    string Operation,
    long? TransactionId,
    DateTimeOffset CommittedAt,
    AttributionKind Attribution,
    string? UserId,
    string? ActorId,
    string? CallingService,
    string? CorrelationId,
    IReadOnlyList<FieldChangeDto> Fields);

/// <summary>One column's value before and after (as text; null when absent or SQL NULL).</summary>
public sealed record FieldChangeDto(string Column, string? Old, string? New);
