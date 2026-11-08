using ProcureAi.Access.Domain;
using ProcureAi.BuildingBlocks.Domain;
using Shouldly;

namespace ProcureAi.Access.Tests;

public sealed class SystemSettingsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_is_enabled_without_message()
    {
        var settings = SystemSettings.Default;

        settings.Enabled.ShouldBeTrue();
        settings.Message.ShouldBeNull();
    }

    [Fact]
    public void Create_keeps_the_switch_the_trimmed_message_and_who_changed_it()
    {
        var settings = SystemSettings.Create(false, "  Paused until Monday.  ", "claude-admin", At);

        settings.Id.ShouldBe(SystemSettings.SingletonId);
        settings.Enabled.ShouldBeFalse();
        settings.Message.ShouldBe("Paused until Monday.");
        settings.ChangedBy.ShouldBe("claude-admin");
        settings.ChangedAt.ShouldBe(At);
    }

    [Fact]
    public void Set_changes_the_switch_and_a_blank_message_clears_it()
    {
        var settings = SystemSettings.Create(false, "Paused.", "claude-admin", At);

        settings.Set(true, "   ", "claude-admin", At.AddHours(1));

        settings.Enabled.ShouldBeTrue();
        settings.Message.ShouldBeNull();
        settings.ChangedAt.ShouldBe(At.AddHours(1));
    }

    [Fact]
    public void A_message_longer_than_the_limit_is_refused()
    {
        var settings = SystemSettings.Create(true, null, "claude-admin", At);

        Should.Throw<DomainException>(() => settings.Set(false, new string('x', SystemSettings.MaxMessageLength + 1), "claude-admin", At));
        settings.Enabled.ShouldBeTrue("a refused change changes nothing");
        settings.Set(false, new string('x', SystemSettings.MaxMessageLength), "claude-admin", At).ShouldBeSameAs(settings);
    }

    [Fact]
    public void Who_changed_it_is_required()
    {
        Should.Throw<DomainException>(() => SystemSettings.Create(true, null, " ", At));
    }
}
