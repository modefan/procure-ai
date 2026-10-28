using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Notifications.Infrastructure;

/// <summary>The <c>Notifications</c> section.</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public const string PushRetrySection = SectionName + ":" + nameof(PushRetry);

    [Required]
    public SignalROptions SignalR { get; set; } = new();

    /// <summary>
    /// Inline retries of a push that failed because Redis (the SignalR backplane) did not answer, before the message goes to the error
    /// queue (<c>Notifications:PushRetry</c>: attempts, initial delay, multiplier, maximum delay). A lost push only means clients refetch
    /// on their next change or reconnect. Scalar values, so configuration replaces each default (an array would be appended to).
    /// </summary>
    [Required]
    public DependencyRetryOptions PushRetry { get; set; } = DefaultPushRetry();

    /// <summary>The defaults: 3 retries after 200 ms, 1 s and 5 s.</summary>
    public static DependencyRetryOptions DefaultPushRetry() => new()
    {
        Attempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(200),
        Multiplier = 5.0,
        MaxDelay = TimeSpan.FromSeconds(5),
    };

    /// <summary>The configured waits before each push retry, validated (an invalid section fails at startup).</summary>
    public static TimeSpan[] PushRetryDelays(IConfiguration configuration)
    {
        var retry = DefaultPushRetry();
        configuration.GetSection(PushRetrySection).Bind(retry);
        return retry.Delays(PushRetrySection);
    }
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
