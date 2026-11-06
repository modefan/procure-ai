namespace ProcureAi.Audit.Domain;

/// <summary>
/// One integration event as the Audit service stored it: who did what, when (the business audit). Stored as a Marten
/// document; never changed after it is written.
/// </summary>
/// <param name="Id">The message ID (Wolverine envelope ID) the publisher gave the event. Every redelivery carries the same one, so storing by it is idempotent.</param>
/// <param name="TenantId">The tenant of the event (from the event itself).</param>
/// <param name="Type">The event's contract name, e.g. <c>ApprovalStepApproved</c>.</param>
/// <param name="SubjectId">The purchase request the event is about, when it names one (<c>RequestId</c>).</param>
/// <param name="UserId">The user the event happened for: the initiating user from the message headers, never the service that sent it.</param>
/// <param name="CorrelationId">The correlation ID of the request that caused it, when the sender had one.</param>
/// <param name="Service">The service that published it (<c>x-calling-service</c>), or <c>unknown</c>.</param>
/// <param name="OccurredAt">When it happened (from the event).</param>
/// <param name="Payload">The event as JSON, as it was received.</param>
public sealed record BusinessEvent(
    Guid Id,
    string TenantId,
    string Type,
    Guid? SubjectId,
    string UserId,
    string? CorrelationId,
    string Service,
    DateTimeOffset OccurredAt,
    string Payload)
{
    public const string UnknownService = "unknown";

    /// <summary>
    /// When Audit stored the event: Marten's <c>mt_last_modified</c> (the database clock at the insert; the store is insert-only, so
    /// it never changes), filled in on every read. Unlike <see cref="OccurredAt"/> a publisher cannot choose it, so it is the order
    /// the events were recorded in (plan 07 carry-over 28). Not set when the event is created.
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}
