using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Status;

/// <summary>Section <c>Status</c>: the status snapshot every instance writes to Redis (plan 06 ruling 9).</summary>
public sealed class StatusOptions : IValidatableObject
{
    public const string SectionName = "Status";

    /// <summary>How often an instance writes its snapshot.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a snapshot lives: an instance that stopped writing drops off after this.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "01:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one part of a snapshot (the readiness checks, the message counts, each contributor) may take; then it is left out.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan PartTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The services the status screen expects (e.g. <c>requests-api</c>); one without a snapshot is <c>Down</c>. Only Notifications reads it
    /// (the AppHost passes <c>Status__ExpectedServices__0=...</c>). Empty: only the services that wrote a snapshot are listed.
    /// </summary>
    public List<string> ExpectedServices { get; set; } = [];

    /// <summary>
    /// A snapshot must be rewritten before the last one expires, even when its parts are slow: <c>Ttl</c> must exceed <c>PublishInterval</c>,
    /// and <c>PartTimeout</c> must be shorter than <c>Ttl - PublishInterval</c>.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Ttl <= PublishInterval)
        {
            yield return new ValidationResult(
                $"'{SectionName}:{nameof(Ttl)}' must be longer than '{SectionName}:{nameof(PublishInterval)}'.", [nameof(Ttl)]);
        }
        else if (PartTimeout >= Ttl - PublishInterval)
        {
            yield return new ValidationResult(
                $"'{SectionName}:{nameof(PartTimeout)}' must be shorter than Ttl - PublishInterval.", [nameof(PartTimeout)]);
        }
    }
}
