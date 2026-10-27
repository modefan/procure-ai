using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Options;

/// <summary>The <c>ErrorResponses</c> configuration section.</summary>
public sealed class ErrorResponseOptions
{
    public const string SectionName = "ErrorResponses";

    /// <summary>
    /// The <c>Retry-After</c> value, in seconds, sent with the 503 for a call to another service that failed or was refused
    /// (timeout, open circuit). Long enough for a circuit to half-open, short enough for a user to retry.
    /// </summary>
    [Range(1, 3600)]
    public int DependencyRetryAfterSeconds { get; set; } = 5;
}
