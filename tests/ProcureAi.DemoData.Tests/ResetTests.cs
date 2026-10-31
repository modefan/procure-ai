using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ProcureAi.DemoData.Tests;

/// <summary>
/// The nightly reset (plan 06 ruling 4, pre-flight ruling P7) against fake services: the order (simulators, the services in parallel, Audit
/// last), the retries of a failed call, a failure that stops the reset with nothing after it, and the next run starting from the beginning.
/// And the schedule: the next 02:00 in Africa/Johannesburg.
/// </summary>
public sealed class ResetTests
{
    private const string Gateway = "https://gateway.test/";
    private const string Simulators = "http://simulators.test/";
    private const string ApiKey = "an-admin-key-for-the-tests-only-0123456789";

    [Fact]
    public async Task Reset_calls_simulators_first_then_the_services_then_audit_last()
    {
        var fake = new FakeServices(_ => HttpStatusCode.NoContent);

        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var order = fake.Requests.Select(Name).ToList();
        order.First().ShouldBe("simulators");
        order.Last().ShouldBe("audit");
        order.Skip(1).SkipLast(1).ShouldBe(ResetRunner.ParallelServices, ignoreOrder: true);
        fake.Requests.ShouldAllBe(r => r.Method == HttpMethod.Post);
        fake.Requests.ShouldAllBe(r => r.Headers.GetValues("X-Api-Key").Single() == ApiKey);
        fake.Requests.Single(r => Name(r) == "simulators").RequestUri!.ToString().ShouldBe(Simulators + "api/v1/simulators/reset");
        fake.Requests.Single(r => Name(r) == "purchasing").RequestUri!.ToString()
            .ShouldBe("https://gateway.test/api/v1/purchasing/internal/demo/reset");
    }

    [Fact]
    public async Task Reset_retries_a_failed_call_three_times()
    {
        var purchasing = 0;
        var fake = new FakeServices(request =>
            Name(request) == "purchasing" && Interlocked.Increment(ref purchasing) <= 3 ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent);

        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        purchasing.ShouldBe(4, "three retries after the first failure");
        fake.Requests.Count(r => Name(r) == "audit").ShouldBe(1);
    }

    [Fact]
    public async Task Reset_resumes_after_failure_mid_way()
    {
        // The first run: Purchasing fails every attempt. The reset stops with a failure once the other services are done, and never reaches
        // Audit (so the audit trail of what happened stays).
        var purchasingDown = true;
        var fake = new FakeServices(request =>
            Name(request) == "purchasing" && Volatile.Read(ref purchasingDown) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        var logs = new ConcurrentQueue<string>();

        (await Runner(fake, logs).RunAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fake.Requests.Count(r => Name(r) == "purchasing").ShouldBe(4);
        fake.Requests.ShouldNotContain(r => Name(r) == "audit");
        logs.ShouldContain(l => l.Contains("purchasing") && l.Contains("503"));

        // The next run starts from the beginning (every step is idempotent) and finishes.
        Volatile.Write(ref purchasingDown, false);
        fake.Requests.Clear();
        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var names = fake.Requests.Select(Name).ToList();
        names.First().ShouldBe("simulators");
        names.Last().ShouldBe("audit");
        names.Distinct().Count().ShouldBe(ResetRunner.ParallelServices.Count + 2);
    }

    [Fact]
    public async Task A_refused_call_is_not_retried()
    {
        // 401, 403, 404: the key or the switch is wrong (DemoReset:Enabled off is 404), and retrying cannot help.
        var fake = new FakeServices(request => Name(request) == "access" ? HttpStatusCode.NotFound : HttpStatusCode.NoContent);

        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fake.Requests.Count(r => Name(r) == "access").ShouldBe(1);
        fake.Requests.ShouldNotContain(r => Name(r) == "audit");
    }

    [Theory]
    // SAST is UTC+2 all year (no daylight saving): 02:00 SAST is 00:00 UTC.
    [InlineData("2026-10-31T10:15:00+02:00", "2026-11-01T02:00:00+02:00")]
    [InlineData("2026-10-31T01:59:59+02:00", "2026-10-31T02:00:00+02:00")]
    [InlineData("2026-10-31T02:00:00+02:00", "2026-11-01T02:00:00+02:00")]
    [InlineData("2026-10-30T23:30:00+00:00", "2026-10-31T02:00:00+02:00")]
    [InlineData("2026-12-31T22:30:00+00:00", "2027-01-01T02:00:00+02:00")]
    [InlineData("2027-01-01T00:00:00+00:00", "2027-01-02T02:00:00+02:00")]
    public void Schedule_computes_next_0200_sast(string now, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

        var next = DemoSchedule.NextRun(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), new TimeSpan(2, 0, 0), zone);

        next.ShouldBe(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture));
        next.Offset.ShouldBe(TimeSpan.FromHours(2));
    }

    private static string Name(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path == "/api/v1/simulators/reset" ? "simulators" : path.Split('/')[3];
    }

    private static ResetRunner Runner(FakeServices fake, ConcurrentQueue<string>? logs = null)
    {
        var options = Options.Create(new DemoDataOptions
        {
            GatewayUrl = Gateway,
            SimulatorUrl = Simulators,
            AdminApiKey = ApiKey,
            ResetCallTimeout = TimeSpan.FromSeconds(10),
            ResetRetries = 3,
            ResetRetryDelay = TimeSpan.Zero,
            AuditGrace = TimeSpan.Zero,
        });
        var clients = new ResetClients(
            new HttpClient(fake, disposeHandler: false) { BaseAddress = new Uri(Gateway) },
            new HttpClient(fake, disposeHandler: false) { BaseAddress = new Uri(Simulators) });
        return new ResetRunner(clients, options, TimeProvider.System, new QueueLogger<ResetRunner>(logs ?? new ConcurrentQueue<string>()));
    }

    private sealed class FakeServices(Func<HttpRequestMessage, HttpStatusCode> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            // The services of the parallel phase overlap, as they do for real.
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            return new HttpResponseMessage(respond(request)) { Content = new StringContent("") };
        }
    }

    private sealed class QueueLogger<T>(ConcurrentQueue<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}
