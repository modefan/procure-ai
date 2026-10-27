using ProcureAi.BuildingBlocks.Application;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Backoff;

public sealed class BackoffOptionsTests
{
    private static BackoffOptions Options(double jitter = 0) => new()
    {
        InitialDelay = TimeSpan.FromSeconds(5),
        Multiplier = 2,
        MaxDelay = TimeSpan.FromMinutes(5),
        JitterFraction = jitter,
    };

    [Fact]
    public void Without_jitter_attempts_1_to_8_double_and_stop_at_the_cap() =>
        Enumerable.Range(1, 8).Select(a => Options().DelayFor(a, new Random(1)).TotalSeconds)
            .ShouldBe([5, 10, 20, 40, 80, 160, 300, 300]);

    [Fact]
    public void Jitter_stays_within_the_fraction_of_the_capped_wait()
    {
        var options = Options(0.5);
        var random = new Random(42);
        foreach (var attempt in Enumerable.Range(1, 8))
        {
            var baseSeconds = options.BaseDelayFor(attempt).TotalSeconds;
            for (var i = 0; i < 200; i++)
            {
                var seconds = options.DelayFor(attempt, random).TotalSeconds;
                seconds.ShouldBeInRange(baseSeconds * 0.5, baseSeconds * 1.5);
            }
        }
    }

    [Fact]
    public void Jitter_actually_varies_and_a_fraction_above_one_never_goes_below_zero()
    {
        var random = new Random(7);
        Enumerable.Range(0, 50).Select(_ => Options(0.5).DelayFor(3, random)).Distinct().Count().ShouldBeGreaterThan(10);
        Enumerable.Range(0, 500).Select(_ => Options(3).DelayFor(1, random)).ShouldAllBe(d => d >= TimeSpan.Zero);
    }

    [Fact]
    public void Attempt_numbers_start_at_1() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Options().DelayFor(0, new Random(1)));
}
