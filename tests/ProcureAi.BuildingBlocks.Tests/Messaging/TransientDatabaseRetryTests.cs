using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Polly.Timeout;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

/// <summary>
/// Final review I1: which failures the shared scheduled retry takes. The end-to-end proof (a spend whose save fails once is retried
/// and booked, nothing dead-lettered) is <c>SpendTests.A_transient_database_failure_while_booking_a_spend_is_retried_and_the_spend_is_booked</c>.
/// </summary>
public sealed class TransientDatabaseRetryTests
{
    private static NpgsqlException ConnectionReset() => new("Exception while writing to stream", new IOException("Connection reset by peer"));

    [Fact]
    public void Transient_database_failures_and_timeouts_are_retried_also_when_wrapped()
    {
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(ConnectionReset()).ShouldBeTrue();
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new DbUpdateException("save failed", ConnectionReset())).ShouldBeTrue();
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new TimeoutException()).ShouldBeTrue();
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(
            new InvalidOperationException("wrapped", new TimeoutException())).ShouldBeTrue();
    }

    [Fact]
    public void Bugs_lost_races_and_failed_service_calls_are_left_to_their_own_rules()
    {
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new InvalidOperationException("bug")).ShouldBeFalse();
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new NpgsqlException("not transient")).ShouldBeFalse();
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new DbUpdateConcurrencyException("lost race")).ShouldBeFalse();
        // The dependency rule (with the service's own DependencyRetry delays) owns these, even when a timeout is inside.
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new TimeoutRejectedException("timed out", new TimeoutException())).ShouldBeFalse();
        TransientDatabaseRetryPolicy.IsTransientDatabaseFailure(new HttpRequestException("503", new TimeoutException())).ShouldBeFalse();
    }

    [Fact]
    public void Default_delays_are_bounded_and_grow_and_can_be_configured()
    {
        TransientDatabaseRetryPolicy.Defaults().Delays().Select(d => d.TotalSeconds).ShouldBe([1, 5, 25, 120]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TransientDatabaseRetryPolicy.SectionName}:Attempts"] = "2",
                [$"{TransientDatabaseRetryPolicy.SectionName}:InitialDelay"] = "00:00:00.200",
            })
            .Build();
        TransientDatabaseRetryPolicy.Delays(configuration).Select(d => d.TotalMilliseconds).ShouldBe([200, 1000]);
    }

    [Fact]
    public void An_invalid_section_fails_with_its_name()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{TransientDatabaseRetryPolicy.SectionName}:Attempts"] = "0" })
            .Build();

        Should.Throw<InvalidOperationException>(() => TransientDatabaseRetryPolicy.Delays(configuration)).Message
            .ShouldContain(TransientDatabaseRetryPolicy.SectionName);
    }
}
