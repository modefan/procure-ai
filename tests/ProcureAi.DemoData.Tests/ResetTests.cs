using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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

        // First the preflight: one GET probe per target, before any reset.
        var probes = fake.Requests.TakeWhile(r => r.Method == HttpMethod.Get).ToList();
        probes.Select(Name).ShouldBe(["simulators", .. ResetRunner.ParallelServices, "audit"], ignoreOrder: true);
        // Then the maintenance flag (plan 07 Task 0b), the resets, and the flag lifted again at the very end.
        var afterProbes = fake.Requests.Skip(probes.Count).ToList();
        afterProbes.First().Method.ShouldBe(HttpMethod.Put);
        afterProbes.First().RequestUri!.ToString().ShouldBe("https://gateway.test/api/v1/gateway/maintenance");
        afterProbes.Last().Method.ShouldBe(HttpMethod.Delete);
        afterProbes.Last().RequestUri!.ToString().ShouldBe("https://gateway.test/api/v1/gateway/maintenance");
        afterProbes.Skip(1).SkipLast(1).ShouldAllBe(r => r.Method == HttpMethod.Post);
        fake.Requests.ShouldAllBe(r => r.Headers.GetValues("X-Api-Key").Single() == ApiKey);

        var order = fake.Posts.Select(Name).ToList();
        order.First().ShouldBe("simulators");
        order.Last().ShouldBe("audit");
        order.Skip(1).SkipLast(1).ShouldBe(ResetRunner.ParallelServices, ignoreOrder: true);
        fake.Posts.Single(r => Name(r) == "simulators").RequestUri!.ToString().ShouldBe(Simulators + "api/v1/simulators/reset");
        fake.Posts.Single(r => Name(r) == "purchasing").RequestUri!.ToString()
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
        fake.Posts.Count(r => Name(r) == "audit").ShouldBe(1);
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

        fake.Posts.Count(r => Name(r) == "purchasing").ShouldBe(4);
        fake.Posts.ShouldNotContain(r => Name(r) == "audit");
        logs.ShouldContain(l => l.Contains("purchasing") && l.Contains("503"));

        // The next run starts from the beginning (every step is idempotent) and finishes.
        Volatile.Write(ref purchasingDown, false);
        fake.Requests.Clear();
        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var names = fake.Posts.Select(Name).ToList();
        names.First().ShouldBe("simulators");
        names.Last().ShouldBe("audit");
        names.Distinct().Count().ShouldBe(ResetRunner.ParallelServices.Count + 2);
    }

    [Fact]
    public async Task A_refused_call_is_not_retried()
    {
        // 401, 403, 404: the key or the switch is wrong, and retrying cannot help. (The preflight saw the route there: 405.)
        var fake = new FakeServices(request => Name(request) == "access" ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);

        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fake.Posts.Count(r => Name(r) == "access").ShouldBe(1);
        fake.Posts.ShouldNotContain(r => Name(r) == "audit");
    }

    [Theory]
    [InlineData("access")]
    [InlineData("audit")]
    [InlineData("simulators")]
    public async Task Reset_switched_off_anywhere_stops_before_the_simulators_are_touched(string off)
    {
        // A deployment with DemoReset:Enabled off answers 404 (the route does not exist). The simulators are reset first and forget every
        // order and restart their numbering, so the preflight probes every target before any reset: nothing at all is reset.
        var fake = new FakeServices(_ => HttpStatusCode.NoContent, probe: r => Name(r) == off ? HttpStatusCode.NotFound : HttpStatusCode.MethodNotAllowed);
        var logs = new ConcurrentQueue<string>();

        (await Runner(fake, logs).RunAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fake.Posts.ShouldBeEmpty();
        fake.Requests.ShouldNotContain(r => r.Method == HttpMethod.Put, "nothing is reset, so no maintenance window either");
        logs.ShouldContain(l => l.Contains(off) && l.Contains("404"));
    }

    [Fact]
    public async Task Reset_lifts_the_maintenance_flag_after_a_failed_call()
    {
        // Purchasing fails every attempt: the reset stops before Audit, but users must not be locked out until the flag's TTL.
        var fake = new FakeServices(request => Name(request) == "purchasing" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);

        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fake.Requests.Count(r => r.Method == HttpMethod.Put && Name(r) == "gateway").ShouldBe(1);
        fake.Requests.Last().Method.ShouldBe(HttpMethod.Delete);
        Name(fake.Requests.Last()).ShouldBe("gateway");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Reset_stops_when_the_maintenance_flag_cannot_be_set(HttpStatusCode answer)
    {
        // A gateway that does not take the flag (an older gateway, a wrong key: 404; Redis down: 5xx after the retries): no reset runs
        // without its window, so users never write into half-reset data.
        var fake = new FakeServices(request => request.Method == HttpMethod.Put ? answer : HttpStatusCode.NoContent);
        var logs = new ConcurrentQueue<string>();

        (await Runner(fake, logs).RunAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();

        fake.Posts.ShouldBeEmpty();
        fake.Requests.ShouldNotContain(r => r.Method == HttpMethod.Delete);
        logs.ShouldContain(l => l.Contains("maintenance") && l.Contains(((int)answer).ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task A_probe_that_gets_no_answer_does_not_stop_the_reset()
    {
        // Only a refusal stops the preflight; a service that is slow or briefly down is the reset call's problem (it retries).
        var fake = new FakeServices(_ => HttpStatusCode.NoContent, probe: r => Name(r) == "budgets" ? HttpStatusCode.BadGateway : HttpStatusCode.MethodNotAllowed);

        (await Runner(fake).RunAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        fake.Posts.Count.ShouldBe(ResetRunner.ParallelServices.Count + 2);
    }

    [Fact]
    public void A_service_gives_up_draining_before_the_reset_call_times_out()
    {
        // A service that cannot drain answers 503 and the call is retried; at the caller's timeout it would be cut off instead, just as it
        // answers. The longest drain a service accepts stays below the call timeout, with room for the clearing that follows.
        var callTimeout = new DemoDataOptions().ResetCallTimeout;

        new ProcureAi.BuildingBlocks.Web.Demo.DemoResetOptions().DrainTimeout.ShouldBeLessThan(callTimeout - TimeSpan.FromSeconds(15));
        TimeSpan.Parse(ProcureAi.BuildingBlocks.Web.Demo.DemoResetOptions.MaxDrainTimeout, CultureInfo.InvariantCulture)
            .ShouldBeLessThan(callTimeout);
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

    [Fact]
    public async Task Schedule_goes_on_to_the_next_night_after_a_reset_that_throws()
    {
        // Not an HTTP failure (those are the reset's own retries) but anything else, e.g. a bug or a broken handler: the night is logged as
        // failed and the loop sleeps until the next 02:00 instead of ending the process for good.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-31T10:00:00+02:00", CultureInfo.InvariantCulture));
        var nights = new ConcurrentQueue<DateTimeOffset>();
        var throwing = new ThrowingHandler(() => nights.Enqueue(time.GetUtcNow()));
        var logs = new ConcurrentQueue<string>();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(typeof(ILogger<>), typeof(SharedQueueLogger<>))
            .AddSingleton(logs)
            .AddSingleton(Runner(throwing, logs, time))
            .BuildServiceProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var schedule = new ScheduleCommand(services, new DemoResetScheduleOptions()).RunAsync(stop.Token);
        for (var i = 0; i < 2000 && nights.Distinct().Count() < 2 && !schedule.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMinutes(30));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        schedule.IsCompleted.ShouldBeFalse(schedule.Exception?.ToString() ?? "the schedule ended");
        nights.Distinct().Take(2).ShouldBe(
        [
            DateTimeOffset.Parse("2026-11-01T02:00:00+02:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-11-02T02:00:00+02:00", CultureInfo.InvariantCulture),
        ]);
        logs.ShouldContain(l => l.Contains("Nightly demo reset failed"));

        await stop.CancelAsync();
        (await schedule).ShouldBe(0);
    }

    private static string Name(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path == "/api/v1/simulators/reset" ? "simulators" : path.Split('/')[3];
    }

    private static ResetRunner Runner(HttpMessageHandler fake, ConcurrentQueue<string>? logs = null, TimeProvider? time = null)
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
        return new ResetRunner(clients, options, time ?? TimeProvider.System, new QueueLogger<ResetRunner>(logs ?? new ConcurrentQueue<string>()));
    }

    /// <summary>
    /// Every reset target. POSTs are answered by <c>respond</c>; the preflight's GET probes by <c>probe</c>, by default 405 (the POST-only
    /// route is there), as a service or the simulators with <c>DemoReset:Enabled</c> on answer.
    /// </summary>
    private sealed class FakeServices(Func<HttpRequestMessage, HttpStatusCode> respond, Func<HttpRequestMessage, HttpStatusCode>? probe = null)
        : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        public IReadOnlyList<HttpRequestMessage> Posts => [.. Requests.Where(r => r.Method == HttpMethod.Post)];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            // The services of the parallel phase overlap, as they do for real.
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            var status = request.Method == HttpMethod.Get ? (probe ?? (_ => HttpStatusCode.MethodNotAllowed))(request) : respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent("") };
        }
    }

    /// <summary>Throws something that is not an HTTP failure on every call, and notes when.</summary>
    private sealed class ThrowingHandler(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            called();
            throw new InvalidOperationException("A bug, not an HTTP failure.");
        }
    }

    private sealed class SharedQueueLogger<T>(ConcurrentQueue<string> lines) : ILogger<T>
    {
        private readonly QueueLogger<T> _inner = new(lines);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class QueueLogger<T>(ConcurrentQueue<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception));
    }
}
