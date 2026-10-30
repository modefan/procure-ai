using System.ComponentModel.DataAnnotations;

namespace ProcureAi.BuildingBlocks.Web.Status;

/// <summary>Section <c>Status</c>: the status snapshot every instance writes to Redis (plan 06 ruling 9).</summary>
public sealed class StatusOptions
{
    public const string SectionName = "Status";

    /// <summary>How often an instance writes its snapshot.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a snapshot lives: an instance that stopped writing drops off after this.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "01:00:00")]
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The services the status screen expects (e.g. <c>requests-api</c>); one without a snapshot is <c>Down</c>. Only Notifications reads it
    /// (the AppHost passes <c>Status__ExpectedServices__0=...</c>). Empty: only the services that wrote a snapshot are listed.
    /// </summary>
    public List<string> ExpectedServices { get; set; } = [];
}
