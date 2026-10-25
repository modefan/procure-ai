using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Gateway.Configuration;

/// <summary>The <c>Session</c> section: the server-side session behind the browser cookie.</summary>
public sealed class BffSessionOptions
{
    public const string SectionName = "Session";

    /// <summary>Refresh the access token this many seconds before it expires.</summary>
    [Range(1, 3600)]
    public int RefreshSkewSeconds { get; set; } = 60;

    /// <summary>A session unused for this long ends (sliding).</summary>
    [Range(1, 43_200)]
    public int IdleTimeoutMinutes { get; set; } = 480;

    /// <summary>Prefix of the Redis keys (sessions, per-user index, data protection keys).</summary>
    [Required(AllowEmptyStrings = false)]
    public string RedisKeyPrefix { get; set; } = "procure:bff:";
}
