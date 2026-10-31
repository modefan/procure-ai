using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Web.Auth;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Demo;

public static class DemoResetEndpoints
{
    public const string Path = "internal/demo/reset";

    /// <summary>
    /// <c>POST {routePrefix}/internal/demo/reset</c> → 204: runs the service's <see cref="IDemoResetter"/>, then clears the service's whole
    /// FusionCache (memory and Redis, other instances through the backplane), which includes the stored <c>[Idempotent]</c> results: a
    /// re-seed after a reset must not get the old answer for an Idempotency-Key whose resource no longer exists. Only for a service account or
    /// the admin API key (<see cref="InternalPolicy"/>), never a user token, not even an admin's.
    /// <para>
    /// Mapped only when <c>DemoReset:Enabled</c> is true: otherwise the route does not exist and every caller gets 404 (fail closed). Not in
    /// the OpenAPI document: it is an operations hook for ProcureAi.DemoData, not part of the API the app uses.
    /// </para>
    /// </summary>
    public static WebApplication MapProcureAiDemoReset(this WebApplication app, string routePrefix)
    {
        var options = app.Configuration.GetSection(DemoResetOptions.SectionName).Get<DemoResetOptions>() ?? new DemoResetOptions();
        if (!options.Enabled)
        {
            return app;
        }

        app.MapPost($"{routePrefix.TrimEnd('/')}/{Path}", ResetAsync)
            .RequireAuthorization(InternalPolicy.Name)
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> ResetAsync(
        IDemoResetter resetter, IFusionCache cache, ICurrentUser caller, ServiceIdentity service, ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(DemoResetEndpoints).FullName!);
        var clock = Stopwatch.StartNew();
        logger.LogWarning("Demo reset of {Service} started by {Caller}", service.Name, caller.IsAdminApiKey ? "the admin key" : caller.UserId);
        await resetter.ResetAsync(cancellationToken);
        // Remove, not expire: no fail-safe may bring back an answer about data that is gone.
        await cache.ClearAsync(allowFailSafe: false, token: CancellationToken.None);
        logger.LogWarning("Demo reset of {Service} done in {Milliseconds} ms", service.Name, clock.ElapsedMilliseconds);
        return Results.NoContent();
    }
}
