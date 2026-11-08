using System.ClientModel;
using Polly.CircuitBreaker;
using ProcureAi.Assistant.Application.Guardrails;
using ProcureAi.Assistant.Infrastructure.Ai;
using Shouldly;

namespace ProcureAi.Assistant.Tests;

/// <summary>
/// Which failed model calls give their token reservation back (<see cref="TokenCounting.CertainlyNotBilled"/>): only those that certainly
/// reached no model. Everything else keeps it, so a call is charged rather than free when in doubt.
/// </summary>
public sealed class TokenCountingTests
{
    public static TheoryData<string, Exception> Refunded => new()
    {
        { "open breaker", new BrokenCircuitException() },
        { "isolated breaker", new IsolatedCircuitException() },
        { "429 rate limit", new ClientResultException("The model provider answered HTTP 429.", Response(429)) },
        { "400 bad request", new ClientResultException("The model provider answered HTTP 400.", Response(400)) },
        { "prompt content filter", new GuardBlockedException(GuardBlockedException.InputStage, GuardReasons.ProviderFilter) },
        { "no connection", ProviderErrors.Scrub(new ClientResultException("x", null, new HttpRequestException(HttpRequestError.ConnectionError))) },
        { "unknown host", new HttpRequestException(HttpRequestError.NameResolutionError) },
    };

    public static TheoryData<string, Exception> Kept => new()
    {
        { "timeout", new TimeoutException() },
        { "500", new ClientResultException("The model provider answered HTTP 500.", Response(500)) },
        { "408", new ClientResultException("The model provider answered HTTP 408.", Response(408)) },
        { "connection broke after sending", new HttpRequestException(HttpRequestError.ResponseEnded) },
        { "output content filter", new GuardBlockedException(GuardBlockedException.OutputStage, GuardReasons.ProviderFilter) },
        { "bug", new InvalidOperationException() },
    };

    [Theory]
    [MemberData(nameof(Refunded))]
    public void Refused_before_any_generation_is_given_back(string name, Exception failure) =>
        TokenCounting.CertainlyNotBilled(failure, CancellationToken.None).ShouldBeTrue(name);

    [Theory]
    [MemberData(nameof(Kept))]
    public void May_have_been_billed_is_kept(string name, Exception failure) =>
        TokenCounting.CertainlyNotBilled(failure, CancellationToken.None).ShouldBeFalse(name);

    [Fact]
    public void The_callers_cancellation_is_always_kept()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        TokenCounting.CertainlyNotBilled(new BrokenCircuitException(), cancelled.Token).ShouldBeFalse();
    }

    [Fact]
    public void A_scrubbed_connection_failure_keeps_only_its_kind()
    {
        var scrubbed = ProviderErrors.Scrub(new ClientResultException("x", null, new HttpRequestException(HttpRequestError.ConnectionError, "secret-ish detail")));

        var inner = scrubbed.InnerException.ShouldBeOfType<HttpRequestException>();
        inner.HttpRequestError.ShouldBe(HttpRequestError.ConnectionError);
        inner.Message.ShouldNotContain("secret-ish");
    }

    private static System.ClientModel.Primitives.PipelineResponse Response(int status) => new StubResponse(status);

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
