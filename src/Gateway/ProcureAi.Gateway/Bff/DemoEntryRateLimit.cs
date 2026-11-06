using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// The <c>demo-entry</c> rate-limit policy (plan 07 Task 0a, plan 08 Task 6, carry-over 10): for each endpoint that carries it,
/// <c>RateLimiting:DemoEntry:PermitLimit</c> calls per <c>RateLimiting:DemoEntry:Window</c> and client, and
/// <c>RateLimiting:DemoEntry:GlobalPermitLimit</c> over every client together. A client is its IPv4 address or its IPv6 prefix
/// (<c>Ipv6PrefixLength</c>, a /64): a host can rotate the low bits of its IPv6 address, so the full address would give it new allowances.
/// The address is the connection's after the forwarded headers (<c>UseProcureAiForwardedHeaders</c> runs before the limiter), so behind a
/// trusted ingress it is the browser's, and a caller that is not a trusted proxy cannot pick its own partition with <c>X-Forwarded-For</c>.
/// A refusal is the pipeline's 429 with <c>Retry-After</c>. In memory per gateway replica (one replica today).
/// </summary>
public static class DemoEntryRateLimit
{
    public const string PolicyName = "demo-entry";

    private const string UnknownClient = "unknown";

    public static IServiceCollection AddDemoEntryRateLimit(this IServiceCollection services)
    {
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<DemoEntryRateLimitOptions>>((limiter, options) =>
            {
                // One global limiter per endpoint, shared by every client partition of that endpoint (the options are a singleton). Lazy, so
                // two first requests in parallel cannot each build one (the loser's replenishment timer would leak and the two would count
                // against different windows).
                var globals = new ConcurrentDictionary<string, Lazy<FixedWindowRateLimiter>>(StringComparer.Ordinal);
                limiter.AddPolicy(PolicyName, http =>
                {
                    var settings = options.Value;
                    // The endpoint, not the path: /BFF/Demo-Login/ reaches the same endpoint and must not get an allowance of its own.
                    var endpoint = http.GetEndpoint()?.DisplayName ?? string.Empty;
                    var global = globals.GetOrAdd(endpoint, _ => new Lazy<FixedWindowRateLimiter>(
                        () => new FixedWindowRateLimiter(Window(settings.GlobalPermitLimit, settings.Window)))).Value;
                    return RateLimitPartition.Get(
                        $"{endpoint}|{ClientKey(http.Connection.RemoteIpAddress, settings.Ipv6PrefixLength)}",
                        _ => new ClientAndGlobalLimiter(new FixedWindowRateLimiter(Window(settings.PermitLimit, settings.Window)), global));
                });
            });
        return services;
    }

    /// <summary>
    /// The partition of a client address: an IPv4 address as is (an IPv4-mapped IPv6 address as its IPv4 address), an IPv6 address cut to
    /// its first <paramref name="ipv6PrefixLength"/> bits, written as <c>prefix/length</c>.
    /// </summary>
    public static string ClientKey(IPAddress? address, int ipv6PrefixLength)
    {
        if (address is null)
        {
            return UnknownClient;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        for (var bit = ipv6PrefixLength; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        return $"{new IPAddress(bytes)}/{ipv6PrefixLength}";
    }

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
    };

    /// <summary>
    /// A client's own limiter in front of the endpoint's global one. The client's is asked first, so a client over its own limit never
    /// spends the global allowance (one noisy client cannot lock everyone out). A full global window refuses before the client's permit is
    /// taken. Disposing it disposes only the client's limiter (the middleware drops idle partitions); the global one lives with the policy.
    /// A fixed-window lease gives nothing back when disposed: a permit taken stays spent for the window. So two races cost a permit that
    /// was not used: at a global window turnover, a client over its own limit spends one global permit; when clients race for the last
    /// global permits, the losers spend a client permit. Both are bounded to the requests in the race. Public for its tests.
    /// </summary>
    public sealed class ClientAndGlobalLimiter(RateLimiter client, RateLimiter global) : RateLimiter
    {
        public override TimeSpan? IdleDuration => client.IdleDuration;

        public override RateLimiterStatistics? GetStatistics() => client.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            // No queue (QueueLimit 0), and the middleware calls AcquireAsync after a refused AttemptAcquire: checking the global window's
            // free permits first means neither call takes a client permit while the global window is full.
            RateLimitLease? globalLease = null;
            if (global.GetStatistics()?.CurrentAvailablePermits < permitCount)
            {
                // Looks full. The window may have just turned over, so ask it: refused is the common case; granted, the client's own
                // limit still applies below (a grant must never skip it), and if the client is refused that global permit stays spent.
                globalLease = global.AttemptAcquire(permitCount);
                if (!globalLease.IsAcquired)
                {
                    return globalLease;
                }
            }

            var clientLease = client.AttemptAcquire(permitCount);
            if (!clientLease.IsAcquired)
            {
                globalLease?.Dispose(); // releases the lease; the global permit stays spent (fixed window)
                return clientLease;
            }

            globalLease ??= global.AttemptAcquire(permitCount);
            if (!globalLease.IsAcquired)
            {
                clientLease.Dispose(); // the client permit stays spent (fixed window): bounded to a race for the last global permits
                return globalLease;
            }

            return new BothLease(clientLease, globalLease);
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override async ValueTask DisposeAsyncCore() => await client.DisposeAsync();
    }

    private sealed class BothLease(RateLimitLease client, RateLimitLease global) : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => client.MetadataNames;

        public override bool TryGetMetadata(string metadataName, out object? metadata) => client.TryGetMetadata(metadataName, out metadata);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                global.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
