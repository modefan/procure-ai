namespace ProcureAi.BuildingBlocks.Persistence;

public enum AuditOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>One audited entity change. Written by <see cref="AuditSaveChangesInterceptor"/> in the same transaction as the change.</summary>
[AuditExclude]
public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public string? TenantId { get; set; }

    /// <summary>The person the change was made for.</summary>
    public string UserId { get; set; } = "";

    /// <summary>Who performed it: the user, <c>claude-admin</c>, or <c>service:&lt;name&gt;</c>. Differs from <see cref="UserId"/> when acting on behalf of someone.</summary>
    public string ActorId { get; set; } = "";

    /// <summary>The service that made the call or sent the message, when known.</summary>
    public string? CallingService { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>
    /// The Postgres transaction ID (the 32-bit xid of <c>pg_current_xact_id()</c>), filled in by the database when the
    /// row is inserted. It equals <c>source.txId</c> of the Debezium message for the same transaction.
    /// </summary>
    public long TransactionId { get; set; }

    public string Table { get; set; } = "";

    public string PrimaryKey { get; set; } = "";

    public AuditOperation Operation { get; set; }

    /// <summary>JSON array of <c>{ "column", "old", "new" }</c>.</summary>
    public string Changes { get; set; } = "[]";

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>One element of <see cref="AuditEntry.Changes"/>.</summary>
public sealed record AuditChange(string Column, object? Old, object? New);
