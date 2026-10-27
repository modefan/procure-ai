namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// Replay protection for webhooks. A handler records the event id in the same unit of work as its effect, so either both
/// are stored or neither is.
/// </summary>
public interface IWebhookInbox
{
    /// <summary>
    /// Adds the event to the current unit of work. Returns <c>false</c> when it is already stored (committed): do nothing.
    /// A concurrent duplicate is only found when the unit of work commits and fails on the key; treat that as already processed.
    /// </summary>
    Task<bool> TryRecordAsync(string source, string eventId, CancellationToken cancellationToken);
}
