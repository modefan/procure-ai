using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

public sealed class DependencyRetryTests
{
    private static DependencyRetryOptions Options() => new()
    {
        Attempts = 8,
        InitialDelay = TimeSpan.FromSeconds(5),
        Multiplier = 2.0,
        MaxDelay = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public void Delays_grow_exponentially_and_are_capped()
    {
        Options().Delays().Select(d => d.TotalSeconds).ShouldBe([5, 10, 20, 40, 80, 160, 300, 300]);
    }

    [Fact]
    public void Invalid_values_are_rejected_clearly_before_they_are_used()
    {
        var shortMax = Options();
        shortMax.MaxDelay = TimeSpan.FromSeconds(1);
        var noAttempts = Options();
        noAttempts.Attempts = 0;

        Should.Throw<InvalidOperationException>(() => shortMax.Delays("Approvals:DependencyRetry")).Message
            .ShouldContain("Approvals:DependencyRetry");
        Should.Throw<InvalidOperationException>(() => noAttempts.Delays());
        Should.Throw<InvalidOperationException>(() => new DependencyRetryOptions().Delays());
    }

    [Fact]
    public void Unreachable_or_failing_dependencies_are_transient()
    {
        DependencyRetryPolicy.IsTransientDependencyFailure(new HttpRequestException("503")).ShouldBeTrue();
        DependencyRetryPolicy.IsTransientDependencyFailure(new TimeoutRejectedException()).ShouldBeTrue();
        DependencyRetryPolicy.IsTransientDependencyFailure(new BrokenCircuitException()).ShouldBeTrue();
        DependencyRetryPolicy.IsTransientDependencyFailure(
            new InvalidOperationException("wrapped", new HttpRequestException("503"))).ShouldBeTrue();
    }

    [Fact]
    public void Answers_and_bugs_are_not_transient()
    {
        DependencyRetryPolicy.IsTransientDependencyFailure(new NotFoundException("no such cost center")).ShouldBeFalse();
        DependencyRetryPolicy.IsTransientDependencyFailure(new InvalidOperationException("bug")).ShouldBeFalse();
        DependencyRetryPolicy.IsTransientDependencyFailure(new ForbiddenException("no")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Http_failures_that_may_pass_are_transient(HttpStatusCode status) =>
        DependencyRetryPolicy.IsTransientDependencyFailure(new HttpRequestException("failed", null, status)).ShouldBeTrue();

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public void Http_failures_that_a_retry_cannot_fix_are_not_transient(HttpStatusCode status)
    {
        DependencyRetryPolicy.IsTransientDependencyFailure(new HttpRequestException("failed", null, status)).ShouldBeFalse();
        DependencyRetryPolicy.IsTransientDependencyFailure(
            new InvalidOperationException("wrapped", new HttpRequestException("failed", null, status))).ShouldBeFalse();
    }
}
