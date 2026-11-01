using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Demo;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Demo;

/// <summary><c>DemoReset</c> is bound with the options pattern and invalid settings fail at startup, not during a nightly reset.</summary>
public sealed class DemoResetOptionsTests
{
    [Fact]
    public void Defaults_are_off_and_valid()
    {
        var options = Resolve([]);

        options.Enabled.ShouldBeFalse("demo-only features are off unless a demo deployment switches them on");
        options.QuietPeriod.ShouldBe(TimeSpan.FromSeconds(3));
        options.DrainTimeout.ShouldBe(TimeSpan.FromSeconds(90));
        options.DrainPollInterval.ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Settings_come_from_the_DemoReset_section()
    {
        var options = Resolve(new()
        {
            ["DemoReset:Enabled"] = "true",
            ["DemoReset:QuietPeriod"] = "00:00:01",
            ["DemoReset:DrainTimeout"] = "00:00:30",
            ["DemoReset:DrainPollInterval"] = "00:00:00.100",
        });

        options.Enabled.ShouldBeTrue();
        options.QuietPeriod.ShouldBe(TimeSpan.FromSeconds(1));
        options.DrainTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.DrainPollInterval.ShouldBe(TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    // At or above the caller's 2 min ResetCallTimeout: the caller would give up just as the service answers 503.
    [InlineData("DemoReset:DrainTimeout", "00:02:00")]
    [InlineData("DemoReset:DrainTimeout", "00:00:00")]
    [InlineData("DemoReset:QuietPeriod", "00:00:00")]
    [InlineData("DemoReset:DrainPollInterval", "00:00:00")]
    [InlineData("DemoReset:DrainPollInterval", "00:00:05")]
    public void Invalid_settings_fail(string key, string value)
    {
        Should.Throw<OptionsValidationException>(() => Resolve(new() { [key] = value }));
    }

    [Fact]
    public void A_quiet_period_as_long_as_the_drain_fails()
    {
        // It could never be reached: every drain would end in 503.
        Should.Throw<OptionsValidationException>(() =>
            Resolve(new() { ["DemoReset:QuietPeriod"] = "00:00:30", ["DemoReset:DrainTimeout"] = "00:00:30" }));
    }

    private static DemoResetOptions Resolve(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var services = new ServiceCollection().AddProcureAiDemoResetOptions(configuration).BuildServiceProvider();
        return services.GetRequiredService<IOptions<DemoResetOptions>>().Value;
    }
}
