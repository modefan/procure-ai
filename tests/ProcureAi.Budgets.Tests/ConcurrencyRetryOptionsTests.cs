using System.ComponentModel.DataAnnotations;
using ProcureAi.Budgets.Application;
using Shouldly;

namespace ProcureAi.Budgets.Tests;

public sealed class ConcurrencyRetryOptionsTests
{
    private static ConcurrencyRetryOptions Options() => new()
    {
        Attempts = 10,
        InitialDelay = TimeSpan.FromMilliseconds(50),
        Multiplier = 2.0,
        MaxDelay = TimeSpan.FromSeconds(2),
        JitterFraction = 0.5,
    };

    [Fact]
    public void Delays_grow_exponentially_are_capped_and_never_exceed_the_maximum()
    {
        var options = Options();

        for (var run = 0; run < 200; run++)
        {
            var delays = options.Delays(new Random(run));

            delays.Length.ShouldBe(10);
            delays.ShouldAllBe(d => d > TimeSpan.Zero && d <= options.MaxDelay);
            delays[0].ShouldBeInRange(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), "50 ms plus or minus 50 percent");
            delays[9].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1), "capped at 2 s, minus at most 50 percent");
        }
    }

    [Fact]
    public void Jitter_makes_the_delays_differ_between_lists()
    {
        var options = Options();

        options.Delays(new Random(1)).ShouldNotBe(options.Delays(new Random(2)));
    }

    [Fact]
    public void The_total_wait_covers_a_burst_of_many_contenders()
    {
        var total = Options().Delays(new Random(3)).Aggregate(TimeSpan.Zero, (sum, d) => sum + d);

        total.ShouldBeGreaterThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Options_without_values_or_with_a_maximum_below_the_start_are_invalid()
    {
        Validate(new BudgetsOptions()).ShouldNotBeEmpty("unset retry options fail at startup");
        Validate(new BudgetsOptions { ConcurrencyRetry = Options() }).ShouldBeEmpty();
        var bad = Options();
        bad.MaxDelay = TimeSpan.FromMilliseconds(10);
        Validate(new BudgetsOptions { ConcurrencyRetry = bad }).ShouldNotBeEmpty();
    }

    private static List<ValidationResult> Validate(BudgetsOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        Validator.TryValidateObject(options.ConcurrencyRetry, new ValidationContext(options.ConcurrencyRetry), results, validateAllProperties: true);
        return results;
    }
}
