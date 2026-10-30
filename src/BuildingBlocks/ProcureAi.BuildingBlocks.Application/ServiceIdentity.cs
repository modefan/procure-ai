namespace ProcureAi.BuildingBlocks.Application;

/// <summary>The name of the service this process runs as (from its composition root), e.g. <c>requests-api</c>.</summary>
public sealed record ServiceIdentity(string Name)
{
    private static readonly string ProcessId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Names this running process: the service name plus an ID that is stable for the life of the process
    /// (e.g. <c>requests-api/3f9a1c20</c>). Used for the <c>X-Instance</c> dev panel header and status snapshots.
    /// </summary>
    public string InstanceId => Name + "/" + ProcessId;
}
