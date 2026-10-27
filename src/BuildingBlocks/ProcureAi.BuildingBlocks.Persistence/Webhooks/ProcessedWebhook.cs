namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>A webhook event already handled: <c>processed_webhooks(source, event_id, received_at)</c>. Technical table: not audited, not captured by CDC.</summary>
[AuditExclude]
public sealed class ProcessedWebhook
{
    public string Source { get; set; } = "";

    public string EventId { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; }
}
