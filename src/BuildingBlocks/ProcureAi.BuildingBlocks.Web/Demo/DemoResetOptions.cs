namespace ProcureAi.BuildingBlocks.Web.Demo;

/// <summary>
/// Section <c>DemoReset</c>. Off by default: the endpoint does not exist (404) unless a demo deployment (the AppHost, later the demo
/// Kubernetes overlay) switches it on with <c>DemoReset__Enabled=true</c>. The nightly time lives with the caller (ProcureAi.DemoData).
/// </summary>
public sealed class DemoResetOptions
{
    public const string SectionName = "DemoReset";

    public bool Enabled { get; set; }
}
