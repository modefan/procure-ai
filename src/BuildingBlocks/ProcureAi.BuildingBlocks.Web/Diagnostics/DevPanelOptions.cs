namespace ProcureAi.BuildingBlocks.Web.Diagnostics;

/// <summary>Section <c>DevPanel</c>: the "behind the scenes" response headers for the Angular dev panel.</summary>
public sealed class DevPanelOptions
{
    public const string SectionName = "DevPanel";

    /// <summary>Off by default; the AppHost and deployments switch it on. Even then, a request must ask with <c>X-Dev-Panel: 1</c>.</summary>
    public bool Enabled { get; set; }
}
