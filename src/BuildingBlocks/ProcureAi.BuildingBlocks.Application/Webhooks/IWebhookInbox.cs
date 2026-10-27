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

/// <summary>Sizes of the <c>processed_webhooks</c> columns, so a receiver can refuse what would not fit before touching the database.</summary>
public static class WebhookLimits
{
    public const int MaxSourceLength = 100;

    /// <summary>
    /// The longest event ID the inbox can store. A receiver answers a longer one with 400 (the sender's error) before saving:
    /// a database error would be a 500, which a sender retries forever.
    /// </summary>
    public const int MaxEventIdLength = 200;
}
