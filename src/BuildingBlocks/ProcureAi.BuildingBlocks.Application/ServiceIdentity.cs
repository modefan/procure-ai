namespace ProcureAi.BuildingBlocks.Application;

/// <summary>The name of the service this process runs as (from its composition root), e.g. <c>requests-api</c>.</summary>
public sealed record ServiceIdentity(string Name);
