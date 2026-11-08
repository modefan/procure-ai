using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Access.Domain;

/// <summary>
/// The system-wide on/off switch of the demo (plan 08 Task 12b): one row, not tenant data. While <see cref="Enabled"/> is false the
/// gateway answers the browser's calls with 503 and the web app shows its maintenance page with <see cref="Message"/>. No row means
/// <see cref="Default"/> (on). Changed only with the admin API key; who and when are kept with it, and the audit interceptor records
/// every change.
/// </summary>
public sealed class SystemSettings
{
    /// <summary>The key of the one row.</summary>
    public const int SingletonId = 1;

    public const int MaxMessageLength = 500;

    private SystemSettings()
    {
    }

    /// <summary>What the system is when nobody ever switched it: on, no message.</summary>
    public static SystemSettings Default => new() { Enabled = true, ChangedBy = SystemCallerId, ChangedAt = DateTimeOffset.MinValue };

    private const string SystemCallerId = "system";

    public int Id { get; private set; } = SingletonId;

    public bool Enabled { get; private set; }

    /// <summary>What the maintenance page says instead of its default text; null for the default.</summary>
    public string? Message { get; private set; }

    /// <summary>Who changed it last (the actor, e.g. <c>claude-admin</c>).</summary>
    public string ChangedBy { get; private set; } = "";

    public DateTimeOffset ChangedAt { get; private set; }

    public static SystemSettings Create(bool enabled, string? message, string changedBy, DateTimeOffset changedAt) =>
        new SystemSettings().Set(enabled, message, changedBy, changedAt);

    /// <summary>Switches the system on or off. A blank message is no message.</summary>
    public SystemSettings Set(bool enabled, string? message, string changedBy, DateTimeOffset changedAt)
    {
        var trimmed = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (trimmed is { Length: > MaxMessageLength })
        {
            throw new DomainException($"The message may be at most {MaxMessageLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(changedBy))
        {
            throw new DomainException("Who changed the system settings is required.");
        }

        Enabled = enabled;
        Message = trimmed;
        ChangedBy = changedBy.Trim();
        ChangedAt = changedAt;
        return this;
    }
}
