using System.Threading.RateLimiting;
using ProcureAi.Gateway.Bff;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// The demo-entry limiter's client-plus-global rule on plain limiters (no host, no Keycloak), for the paths the HTTP tests cannot time:
/// a global window that turns over between the statistics read and the acquire (plan 08 Task 6 fix round 1, review M2).
/// </summary>
public sealed class ClientAndGlobalLimiterTests
{
    [Fact]
    public void A_global_window_that_turns_over_does_not_let_a_client_past_its_own_limit()
    {
        using var client = Manual(1);
        client.AttemptAcquire().IsAcquired.ShouldBeTrue("the client's only permit of the window");
        // Reports a full window, then grants: the window turned over between the two calls.
        using var global = new TurningOverLimiter();
        using var limiter = new DemoEntryRateLimit.ClientAndGlobalLimiter(client, global);

        using var lease = limiter.AttemptAcquire();

        lease.IsAcquired.ShouldBeFalse("the client is over its own limit even though the global window granted a permit");
        global.Grants.ShouldBe(1);
    }

    [Fact]
    public void A_client_over_its_own_limit_does_not_spend_the_global_allowance()
    {
        using var client = Manual(1);
        using var global = Manual(2);
        using var limiter = new DemoEntryRateLimit.ClientAndGlobalLimiter(client, global);

        limiter.AttemptAcquire().IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire().IsAcquired.ShouldBeFalse();

        global.GetStatistics()!.CurrentAvailablePermits.ShouldBe(1);
    }

    [Fact]
    public void A_full_global_window_refuses_without_taking_the_clients_permit()
    {
        using var client = Manual(1);
        using var global = Manual(1);
        global.AttemptAcquire().IsAcquired.ShouldBeTrue("another client took the last global permit");
        using var limiter = new DemoEntryRateLimit.ClientAndGlobalLimiter(client, global);

        limiter.AttemptAcquire().IsAcquired.ShouldBeFalse();

        client.GetStatistics()!.CurrentAvailablePermits.ShouldBe(1);
    }

    private static FixedWindowRateLimiter Manual(int permits) => new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = false,
    });

    private sealed class TurningOverLimiter : RateLimiter
    {
        public int Grants { get; private set; }

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics GetStatistics() => new() { CurrentAvailablePermits = 0 };

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            Grants++;
            return new Granted();
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));

        private sealed class Granted : RateLimitLease
        {
            public override bool IsAcquired => true;

            public override IEnumerable<string> MetadataNames => [];

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                metadata = null;
                return false;
            }
        }
    }
}
