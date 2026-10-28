using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Notifications.Infrastructure;

/// <summary>The <c>Notifications</c> section.</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    [Required]
    public SignalROptions SignalR { get; set; } = new();

    /// <summary>
    /// Inline retries of a push that failed because Redis (the SignalR backplane) did not answer, before the message goes to the
    /// error queue. A lost push only means clients refetch on their next change or reconnect.
    /// </summary>
    [MinLength(1)]
    public TimeSpan[] PushRetryDelays { get; set; } = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];
}

/// <summary><c>Notifications:SignalR</c>.</summary>
public sealed class SignalROptions
{
    /// <summary>
    /// Prefix of the Redis channels of the SignalR backplane, so the hub's messages never mix with another app's on the same Redis.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string ChannelPrefix { get; set; } = "procure-signalr";
}
