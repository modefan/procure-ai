using System.Globalization;
using System.Net.Http.Headers;
using ProcureAi.BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Access;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Options;
using ProcureAi.Gateway.Configuration;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.Gateway.Bff;

/// <summary>The demo's on/off switch as Access answers it (<c>GET api/v1/system/availability</c>).</summary>
public sealed record SystemAvailabilityState(bool Enabled, string? Message)
{
    public static readonly SystemAvailabilityState On = new(true, null);
}

/// <summary>Reads the switch from Access.</summary>
public interface ISystemAvailabilityClient
{
    Task<SystemAvailabilityState> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The gateway's read of Access's switch. It is made as the gateway (its client-credentials token and <c>X-Calling-Service</c>), so Access
/// counts it in the internal service accounts' rate-limit partition (the shared <c>procure-internal</c> client, which the other services'
/// calls to Access use too), not in the anonymous one: the anonymous browser reads that the gateway proxies all share the gateway pod's
/// address at Access, and a flood of them must not make this read fail (a freshly started gateway would then fail open). The partition
/// comes from a validated token, so no visitor can reach it or claim it with a header. When no token can be had quickly (Keycloak down or
/// slow), the read is made anonymously as before: the endpoint is public, and a Keycloak outage must not turn into "cannot read the switch".
/// Fails fast (<see cref="SystemAvailabilityOptions"/>' retries and timeouts): it sits in front of every browser call.
/// </summary>
public sealed class SystemAvailabilityClient(
    HttpClient http, ServiceTokenProvider tokens, ServiceIdentity service, IOptions<SystemAvailabilityOptions> options,
    ILogger<SystemAvailabilityClient> logger) : ISystemAvailabilityClient
{
    public const string Path = "api/v1/system/availability";

    public async Task<SystemAvailabilityState> GetAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        if (await TokenAsync(cancellationToken) is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation(ApiKeyDefaults.CallingServiceHeader, service.Name);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SystemAvailabilityState>(cancellationToken)
            ?? throw new InvalidOperationException("Access answered the system switch with an empty body.");
    }

    private async Task<string?> TokenAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.TokenTimeout);
        try
        {
            return await tokens.GetTokenAsync(timeout.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("No service token for the system switch read ({Reason}); reading it anonymously", exception.Message);
            return null;
        }
    }
}

/// <summary>
/// The switch, cached in FusionCache for <see cref="SystemAvailabilityOptions.CacheDuration"/> (identical concurrent reads share one call to
/// Access). The gateway has no message bus, so it does not hear a change: the short duration is how a change arrives. With Access
/// unreachable the last answer is kept (fail-safe, up to <see cref="SystemAvailabilityOptions.FailSafeMaxDuration"/>); when there never was
/// one, the demo counts as on (fail open, logged): an unreadable switch must never lock the demo.
/// </summary>
public sealed class SystemAvailabilityReader(
    ISystemAvailabilityClient client, IFusionCache cache, IOptions<SystemAvailabilityOptions> options, ILogger<SystemAvailabilityReader> logger)
{
    public const string CacheKey = "gateway:system-availability";

    public async ValueTask<SystemAvailabilityState> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        return await cache.GetOrSetAsync<SystemAvailabilityState>(
            CacheKey,
            async (context, token) =>
            {
                try
                {
                    return await client.GetAsync(token);
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    if (context.HasStaleValue)
                    {
                        logger.LogWarning("The system switch could not be read from Access ({Reason}); the last answer is kept", exception.Message);
                        throw;
                    }

                    // Never read: fail open, and ask again only after the throttle, so an Access outage does not slow every call down.
                    logger.LogWarning("The system switch could not be read from Access ({Reason}); the demo counts as on", exception.Message);
                    context.Options.Duration = settings.FailSafeThrottleDuration;
                    return SystemAvailabilityState.On;
                }
            },
            options: new FusionCacheEntryOptions
            {
                Duration = settings.CacheDuration,
                IsFailSafeEnabled = true,
                FailSafeMaxDuration = settings.FailSafeMaxDuration,
                FailSafeThrottleDuration = settings.FailSafeThrottleDuration,
            },
            token: cancellationToken);
    }
}

/// <summary>
/// While the demo is switched off, every browser call to <see cref="SystemAvailabilityOptions.BlockedPaths"/> (the API, the BFF and the hubs)
/// is answered with 503 ProblemDetails of type <see cref="ProblemType"/> and <c>Retry-After</c>, before authentication and before anything is
/// proxied, so nothing a visitor does reaches a service; the web app shows its maintenance page on that type. Passes: the
/// <see cref="SystemAvailabilityOptions.AllowedPaths"/> (the switch itself), everything outside the blocked paths (health, Swagger UI, the
/// web app's files and <c>config.json</c>), and calls with the valid admin API key (a forged <c>X-Api-Key</c> does not pass). Separate from
/// the nightly reset's <see cref="MaintenanceMiddleware"/>, which only holds back writes for a few minutes.
/// </summary>
public sealed class SystemAvailabilityMiddleware(RequestDelegate next)
{
    public const string ProblemType = "https://procure-ai/problems/system-disabled";

    public const string Title = "This demo is switched off";

    /// <summary>
    /// The ProblemDetails extension with the stored message exactly as Access answers it (null: none), so the web app shows the same texts
    /// whether it read the switch itself or got this 503; <c>detail</c> falls back to <see cref="DefaultDetail"/> for other clients.
    /// </summary>
    public const string MessageExtension = "message";

    public const string DefaultDetail =
        "The demo is paused to save hosting costs. It can be switched on again on request.";

    public async Task InvokeAsync(
        HttpContext context, SystemAvailabilityReader reader, ApiKeyValidator apiKeys, IOptions<SystemAvailabilityOptions> options)
    {
        if (Applies(context.Request.Path, options.Value) && !apiKeys.IsValid(context.Request))
        {
            var state = await reader.GetAsync(context.RequestAborted);
            if (!state.Enabled)
            {
                context.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(options.Value.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                var message = string.IsNullOrWhiteSpace(state.Message) ? null : state.Message.Trim();
                await Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable, title: Title, detail: message ?? DefaultDetail, type: ProblemType,
                        extensions: new Dictionary<string, object?> { [MessageExtension] = message })
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    /// <summary>Below a blocked path and not below an allowed one.</summary>
    public static bool Applies(PathString path, SystemAvailabilityOptions options) =>
        options.BlockedPaths.Any(blocked => path.StartsWithSegments(blocked, StringComparison.OrdinalIgnoreCase))
        && !options.AllowedPaths.Any(allowed => path.StartsWithSegments(allowed, StringComparison.OrdinalIgnoreCase));
}

public static class SystemAvailabilitySetup
{
    /// <summary>
    /// The switch's options, its reader (on the FusionCache the gateway registers) and the fast-failing client of Access, which reads as the
    /// gateway's service account (<c>Auth:Internal</c>, the same login as the gateway's other Access clients).
    /// </summary>
    public static IServiceCollection AddSystemAvailability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<SystemAvailabilityOptions>(configuration, SystemAvailabilityOptions.SectionName);
        services.AddProcureAiServiceTokens();
        services.AddHttpClient<ISystemAvailabilityClient, SystemAvailabilityClient>(
                client => client.BaseAddress = new Uri($"http://{AccessClientSetup.AccessServiceName}"))
            .WithResilienceFrom<SystemAvailabilityOptions>();
        services.AddScoped<SystemAvailabilityReader>();
        return services;
    }
}
