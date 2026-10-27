using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ProcureAi.BuildingBlocks.Web.Webhooks;

/// <summary>The <c>Webhooks</c> configuration section.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    public const int MinSecretLength = 32;

    /// <summary>How far a signature timestamp may differ from our clock, in either direction.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The largest body accepted; a bigger request is refused before it is read in full.</summary>
    [Range(1, 100_000_000)]
    public long MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>One entry per source (e.g. <c>supplier</c>), each with its own secret.</summary>
    public Dictionary<string, WebhookSourceOptions> Sources { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WebhookSourceOptions
{
    /// <summary>The shared HMAC secret (from user secrets or a Kubernetes secret, never the repo).</summary>
    public string Secret { get; set; } = "";
}

internal sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOptions options)
    {
        var failures = options.Sources
            .Where(s => (s.Value.Secret?.Length ?? 0) < WebhookOptions.MinSecretLength)
            .Select(s => $"Webhooks:Sources:{s.Key}:Secret must be at least {WebhookOptions.MinSecretLength} characters.")
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
