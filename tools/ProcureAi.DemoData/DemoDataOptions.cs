using System.ComponentModel.DataAnnotations;

namespace ProcureAi.DemoData;

/// <summary>Configuration section <c>DemoData</c>. Defaults live in appsettings.json; the AppHost sets the gateway URL and the admin key.</summary>
public sealed class DemoDataOptions
{
    public const string Section = "DemoData";

    /// <summary>The gateway's base URL: every call goes through it, like the app's own.</summary>
    [Required, Url]
    public string GatewayUrl { get; set; } = "";

    /// <summary>The admin API key (a secret: AppHost parameter <c>admin-api-key</c>, a Kubernetes secret when deployed). Never logged.</summary>
    [Required, MinLength(32)]
    public string AdminApiKey { get; set; } = "";

    /// <summary>How long one asynchronous transition (a saga step, a supplier webhook, an invoice match) may take before the seed fails.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:30:00")]
    public TimeSpan StepTimeout { get; set; }

    /// <summary>The wait between two reads while waiting for a transition. Reads are micro-cached for 1 s, so polling faster gains nothing.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan PollInterval { get; set; }

    /// <summary>How many requests of one tenant are driven at the same time.</summary>
    [Range(1, 16)]
    public int MaxParallel { get; set; }

    /// <summary>Where the scenario files are, relative to the tool's folder unless absolute.</summary>
    [Required]
    public string ScenarioDirectory { get; set; } = "";

    /// <summary>
    /// Accept the gateway's certificate without validation. Only for the local AppHost, whose gateway serves the ASP.NET Core development
    /// certificate; off by default, so a deployment validates.
    /// </summary>
    public bool AllowUntrustedServerCertificate { get; set; }

    public string ScenarioPath => Path.IsPathRooted(ScenarioDirectory) ? ScenarioDirectory : Path.Combine(AppContext.BaseDirectory, ScenarioDirectory);
}
