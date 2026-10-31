namespace ProcureAi.BuildingBlocks.Web.Demo;

/// <summary>
/// A service's nightly demo reset (plan 06 ruling 4): deletes its business data and its Wolverine envelopes, then writes its own demo seed
/// again. Registered by the service's Infrastructure; called by <c>POST {route prefix}/internal/demo/reset</c>
/// (<see cref="DemoResetEndpoints.MapProcureAiDemoReset"/>). Running it twice in a row must be safe and leave the same data.
/// <para>
/// Deletes go through the app's own write paths (EF Core with the audit interceptor, <c>TenantCollection</c> for MongoDB), so change data
/// capture sees ordinary app changes, never "changed outside the app" (plan 06 ruling 5).
/// </para>
/// </summary>
public interface IDemoResetter
{
    Task ResetAsync(CancellationToken cancellationToken);
}
