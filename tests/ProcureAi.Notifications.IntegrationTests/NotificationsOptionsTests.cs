using Microsoft.Extensions.Configuration;
using ProcureAi.Notifications.Infrastructure;
using Shouldly;

namespace ProcureAi.Notifications.IntegrationTests;

/// <summary>
/// <c>Notifications:PushRetry</c> is made of scalars, so configuration replaces each default. (The first shape, an array of delays,
/// was appended to by the configuration binder: three configured delays on top of three defaults gave six retries, and fewer could
/// never be configured.)
/// </summary>
public sealed class NotificationsOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Without_configuration_the_defaults_apply()
    {
        NotificationsOptions.PushRetryDelays(Config([])).ShouldBe(
            [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)]);
    }

    [Fact]
    public void Configuration_replaces_the_defaults_also_with_fewer_retries()
    {
        var delays = NotificationsOptions.PushRetryDelays(Config(new()
        {
            [$"{NotificationsOptions.PushRetrySection}:Attempts"] = "1",
            [$"{NotificationsOptions.PushRetrySection}:InitialDelay"] = "00:00:00.050",
        }));

        delays.ShouldBe([TimeSpan.FromMilliseconds(50)]);
    }

    [Fact]
    public void Invalid_configuration_fails()
    {
        Should.Throw<InvalidOperationException>(() => NotificationsOptions.PushRetryDelays(Config(new()
        {
            [$"{NotificationsOptions.PushRetrySection}:Attempts"] = "0",
        })));
    }
}
