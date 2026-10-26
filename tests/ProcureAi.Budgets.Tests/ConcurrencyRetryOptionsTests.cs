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
    public void Delays_grow_exponentially_and_are_capped()
    {
        var delays = Options().Delays();

        delays.Select(d => d.TotalMilliseconds).ShouldBe([50, 100, 200, 400, 800, 1600, 2000, 2000, 2000, 2000]);
    }

    [Fact]
    public void Retries_total_enough_waiting_for_a_burst_of_many_contenders()
    {
        var total = Options().Delays().Aggregate(TimeSpan.Zero, (sum, d) => sum + d);

        total.ShouldBeGreaterThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Invalid_values_are_rejected_clearly_before_they_are_used()
    {
        var negative = Options();
        negative.Attempts = -1;
        var shortMax = Options();
        shortMax.MaxDelay = TimeSpan.FromMilliseconds(10);

        Should.Throw<InvalidOperationException>(() => negative.Delays()).Message.ShouldContain("Attempts");
        Should.Throw<InvalidOperationException>(() => shortMax.Delays()).Message.ShouldContain("MaxDelay");
        Should.Throw<InvalidOperationException>(() => new ConcurrencyRetryOptions().Delays());
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
