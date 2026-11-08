using System.ClientModel;
using Polly.CircuitBreaker;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Infrastructure.Ai;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

/// <summary>
/// How a failed model call that reported no usage is charged (<see cref="TokenCounting.OnFailure"/>), after Azure's billing rules: nothing
/// only when it certainly processed nothing, the prompt alone when the prompt was evaluated and refused (any 400, the prompt content
/// filter), the whole reservation otherwise, so a call is charged rather than free when in doubt.
/// </summary>
public sealed class TokenCountingTests
{
    public static TheoryData<string, Exception> Refunded => new()
    {
        { "open breaker", new BrokenCircuitException() },
        { "isolated breaker", new IsolatedCircuitException() },
        { "429 rate limit", Status(429) },
        { "401 unauthorized", Status(401) },
        { "403 forbidden", Status(403) },
        { "404 no such deployment", Status(404) },
        { "no connection", ProviderErrors.Scrub(new ClientResultException("x", null, new HttpRequestException(HttpRequestError.ConnectionError))) },
        { "unknown host", new HttpRequestException(HttpRequestError.NameResolutionError) },
    };

    public static TheoryData<string, Exception> PromptOnly => new()
    {
        { "400 bad request", Status(400) },
        { "prompt content filter", new GuardBlockedException(GuardBlockedException.InputStage, GuardReasons.ProviderFilter) },
    };

    public static TheoryData<string, Exception> Kept => new()
    {
        { "timeout", new TimeoutException() },
        { "500", Status(500) },
        { "408", Status(408) },
        { "422", Status(422) },
        { "connection broke after sending", new HttpRequestException(HttpRequestError.ResponseEnded) },
        { "output content filter", new GuardBlockedException(GuardBlockedException.OutputStage, GuardReasons.ProviderFilter) },
        { "bug", new InvalidOperationException() },
    };

    [Theory]
    [MemberData(nameof(Refunded))]
    public void Refused_before_any_processing_is_given_back(string name, Exception failure) =>
        TokenCounting.OnFailure(failure, CancellationToken.None).ShouldBe(FailedCallCharge.Refund, name);

    [Theory]
    [MemberData(nameof(PromptOnly))]
    public void A_refused_prompt_is_charged_for_its_evaluation(string name, Exception failure) =>
        TokenCounting.OnFailure(failure, CancellationToken.None).ShouldBe(FailedCallCharge.PromptOnly, name);

    [Theory]
    [MemberData(nameof(Kept))]
    public void May_have_been_billed_is_kept(string name, Exception failure) =>
        TokenCounting.OnFailure(failure, CancellationToken.None).ShouldBe(FailedCallCharge.Keep, name);

    [Fact]
    public void The_charges_settle_with_nothing_the_prompt_or_the_reservation()
    {
        var estimate = new TokenEstimate(Prompt: 300, Output: 800);

        TokenCounting.Charge(FailedCallCharge.Refund, estimate).ShouldBe(0);
        TokenCounting.Charge(FailedCallCharge.PromptOnly, estimate).ShouldBe(300);
        TokenCounting.Charge(FailedCallCharge.Keep, estimate).ShouldBeNull();
    }

    [Fact]
    public void The_callers_cancellation_is_always_kept()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        TokenCounting.OnFailure(new BrokenCircuitException(), cancelled.Token).ShouldBe(FailedCallCharge.Keep);
        TokenCounting.OnFailure(Status(429), cancelled.Token).ShouldBe(FailedCallCharge.Keep);
    }

    [Fact]
    public void A_scrubbed_connection_failure_keeps_only_its_kind()
    {
        var scrubbed = ProviderErrors.Scrub(new ClientResultException("x", null, new HttpRequestException(HttpRequestError.ConnectionError, "secret-ish detail")));

        var inner = scrubbed.InnerException.ShouldBeOfType<HttpRequestException>();
        inner.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        inner.Message.ShouldNotContain("secret-ish");
    }

    private static ClientResultException Status(int status) =>
        new($"The model provider answered HTTP {status}.", new StubResponse(status));

    private sealed class StubResponse(int status) : System.ClientModel.Primitives.PipelineResponse
    {
        public override int Status => status;

        public override string ReasonPhrase => "";

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString("{}");

        protected override System.ClientModel.Primitives.PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(Content);

        public override void Dispose()
        {
        }
    }
}
