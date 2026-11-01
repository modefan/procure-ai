using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Gateway.Configuration;
using StackExchange.Redis;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The maintenance flag of the nightly demo reset (plan 07 Task 0b, carry-over 6): a Redis key with a TTL
/// (<see cref="MaintenanceOptions.MaintenanceTtl"/>), so every gateway instance sees it and a crashed reset never locks the app. Only the
/// gateway reads and writes it; <c>ProcureAi.DemoData reset</c> sets and lifts it through <see cref="MaintenanceEndpoints"/>.
/// </summary>
public sealed class MaintenanceFlag(
    IConnectionMultiplexer redis, IOptions<MaintenanceOptions> options, TimeProvider time, ILogger<MaintenanceFlag> logger)
{
    private RedisKey Key => options.Value.MaintenanceKey;

    /// <summary>
    /// True while the flag is set. Fails open: with Redis unreachable the flag cannot be read, and the sessions (also in Redis) fail anyway;
    /// the window is a courtesy to users, not a security control.
    /// </summary>
    public async Task<bool> IsSetAsync()
    {
        try
        {
            return await redis.GetDatabase().KeyExistsAsync(Key);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            logger.LogWarning("The maintenance flag could not be read ({Reason}); writes are let through", exception.Message);
            return false;
        }
    }

    /// <summary>When the flag was set, or null when it is not.</summary>
    public async Task<DateTimeOffset?> SinceAsync()
    {
        var value = await redis.GetDatabase().StringGetAsync(Key);
        return value.HasValue && DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since)
            ? since
            : null;
    }

    /// <summary>Sets the flag (again: a second call restarts its TTL).</summary>
    public Task SetAsync() =>
        redis.GetDatabase().StringSetAsync(Key, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), options.Value.MaintenanceTtl);

    public Task ClearAsync() => redis.GetDatabase().KeyDeleteAsync(Key);
}

/// <summary>
/// While the flag is set, every write (not GET, HEAD, OPTIONS or TRACE) to <c>/api/**</c> and <c>/bff/**</c> is answered with 503
/// ProblemDetails and <c>Retry-After</c>, before authentication and before anything is proxied, so no user writes into half-reset data.
/// Passes: <c>/bff/logout</c> (leaving is always possible) and calls with the valid admin API key (the reset and the seed themselves; a
/// forged <c>X-Api-Key</c> header does not pass). The Angular app shows the ProblemDetails text as it shows any 503.
/// </summary>
public sealed class MaintenanceMiddleware(RequestDelegate next)
{
    public const string Title = "The demo is being reset";

    public const string Detail = "The demo data is being reset to its starting state. Please try again in a few minutes.";

    public async Task InvokeAsync(HttpContext context, MaintenanceFlag flag, ApiKeyValidator apiKeys, IOptions<MaintenanceOptions> options)
    {
        if (Applies(context.Request) && !apiKeys.IsValid(context.Request) && await flag.IsSetAsync())
        {
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(options.Value.MaintenanceRetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: Title, detail: Detail).ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    /// <summary>A write to the API or the BFF, other than logout.</summary>
    public static bool Applies(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
          || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && (request.Path.StartsWithSegments("/api")
            || (request.Path.StartsWithSegments("/bff") && !request.Path.StartsWithSegments("/bff/logout")));
}

/// <summary>
/// <c>PUT api/v1/gateway/maintenance</c> sets the flag (204), <c>DELETE</c> lifts it (204, also when it was not set), <c>GET</c> answers
/// <c>{ active, since }</c>. Only for the valid admin API key; anyone else gets 404, as if the route did not exist. Not in any OpenAPI
/// document: an operations hook for ProcureAi.DemoData.
/// </summary>
public static class MaintenanceEndpoints
{
    public const string Path = "/api/v1/gateway/maintenance";

    public static IServiceCollection AddMaintenanceWindow(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<MaintenanceOptions>(configuration, MaintenanceOptions.SectionName);
        // The admin key's SHA-256, as every service has it (Auth:AdminApiKeySha256; empty: no call is the admin key's). Only that value is
        // checked: the gateway does not validate tokens, so the rest of the services' Auth section (authority, audience) is not required here.
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(
                o => string.IsNullOrEmpty(o.AdminApiKeySha256) || (o.AdminApiKeySha256.Length == 64 && o.AdminApiKeySha256.All(Uri.IsHexDigit)),
                "Auth:AdminApiKeySha256 must be 64 hexadecimal characters or empty.")
            .ValidateOnStart();
        services.TryAddSingleton<ApiKeyValidator>();
        services.AddSingleton<MaintenanceFlag>();
        return services;
    }

    public static IEndpointRouteBuilder MapMaintenance(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, StatusAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapPut(Path, SetAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapDelete(Path, ClearAsync).AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> StatusAsync(HttpContext http, ApiKeyValidator apiKeys, MaintenanceFlag flag)
    {
        if (!apiKeys.IsValid(http.Request))
        {
            return NotFound();
        }

        var since = await flag.SinceAsync();
        return Results.Json(new { active = since is not null, since });
    }

    private static async Task<IResult> SetAsync(HttpContext http, ApiKeyValidator apiKeys, MaintenanceFlag flag, ILogger<MaintenanceFlag> logger)
    {
        if (!apiKeys.IsValid(http.Request))
        {
            return NotFound();
        }

        await flag.SetAsync();
        logger.LogWarning("Maintenance window opened by {CallingService}: users' writes get 503", CallingService(http));
        return Results.NoContent();
    }

    private static async Task<IResult> ClearAsync(HttpContext http, ApiKeyValidator apiKeys, MaintenanceFlag flag, ILogger<MaintenanceFlag> logger)
    {
        if (!apiKeys.IsValid(http.Request))
        {
            return NotFound();
        }

        await flag.ClearAsync();
        logger.LogWarning("Maintenance window closed by {CallingService}", CallingService(http));
        return Results.NoContent();
    }

    /// <summary>The caller's <c>X-Calling-Service</c>, for the log only (letters, digits, '-' and '.', at most 64; anything else is not logged).</summary>
    private static string CallingService(HttpContext http)
    {
        var name = http.Request.Headers[ApiKeyDefaults.CallingServiceHeader].ToString();
        return name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? name : "an unnamed caller";
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
