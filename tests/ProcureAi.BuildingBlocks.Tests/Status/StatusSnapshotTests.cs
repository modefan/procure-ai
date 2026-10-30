using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Status;
using ProcureAi.BuildingBlocks.Messaging.Status;
using ProcureAi.BuildingBlocks.Tests.Web;
using ProcureAi.BuildingBlocks.Web.Http;
using ProcureAi.BuildingBlocks.Web.Status;
using Shouldly;
using StackExchange.Redis;
using Wolverine;
using Wolverine.RabbitMQ;

namespace ProcureAi.BuildingBlocks.Tests.Status;

/// <summary>The status snapshot every instance writes to Redis (plan 06 ruling 9) and the breaker registry it reports (ruling 10).</summary>
[Collection(RedisCollection.Name)]
public sealed class StatusSnapshotTests(RedisFixture redis)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Counts up on every snapshot, so a test can see that a newer one was written.</summary>
    private sealed class CountingContributor : IStatusContributor
    {
        private static long _calls;

        public string Name => "counting";

        public Task<StatusSection> CollectAsync(CancellationToken ct) => Task.FromResult(new StatusSection(
            Name, new Dictionary<string, long> { [StatusCounters.Key("acme", "calls")] = Interlocked.Increment(ref _calls) }, []));
    }

    private sealed class FailingContributor : IStatusContributor
    {
        public string Name => "failing";

        public Task<StatusSection> CollectAsync(CancellationToken ct) => throw new InvalidOperationException("broken contributor");
    }

    /// <summary>Never answers (a dependency that hangs): only the part timeout ends it.</summary>
    private sealed class HangingContributor : IStatusContributor
    {
        public string Name => "hanging";

        public async Task<StatusSection> CollectAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private IHost BuildHost(
        string service, IConnectionMultiplexer multiplexer, TimeSpan interval, TimeSpan ttl, bool hanging = false, TimeSpan? partTimeout = null) =>
        Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Status:PublishInterval"] = interval.ToString(),
                ["Status:Ttl"] = ttl.ToString(),
                ["Status:PartTimeout"] = (partTimeout ?? TimeSpan.FromMilliseconds(200)).ToString(),
            }))
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(multiplexer);
                services.AddSingleton(new ServiceIdentity(service));
                services.AddHealthChecks();
                services.AddProcureAiStatus(context.Configuration);
                services.AddStatusContributor<CountingContributor>();
                services.AddStatusContributor<FailingContributor>();
                if (hanging)
                {
                    services.AddStatusContributor<HangingContributor>();
                }
            })
            .Build();

    [Fact]
    public async Task Snapshot_written_with_ttl_and_refreshed()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var service = $"snap-{Guid.NewGuid():N}"[..13];
        using var host = BuildHost(service, multiplexer, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
        var key = StatusKeys.Snapshot(service, new ServiceIdentity(service).InstanceId);
        var db = multiplexer.GetDatabase();

        await host.StartAsync(Ct);
        var first = await WaitForAsync(db, key, _ => true);
        var ttl = await db.KeyTimeToLiveAsync(key);
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeInRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        var expiresAt = await db.SortedSetScoreAsync(StatusKeys.Index, key);
        expiresAt.ShouldNotBeNull("the reader finds snapshots through the index");
        DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAt.Value).ShouldBeInRange(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(6));

        first.Service.ShouldBe(service);
        first.InstanceId.ShouldBe(new ServiceIdentity(service).InstanceId);
        first.Health.ShouldBe("Healthy", "no readiness checks registered: healthy");
        first.Messages.ShouldBe(WolverineCountsDto.None, "no Wolverine in this host");
        first.Sections.Select(s => s.Name).ShouldBe(["counting"], "a failing contributor is left out, the snapshot is still written");
        var calls = first.Sections[0].Counters[StatusCounters.Key("acme", "calls")];

        var refreshed = await WaitForAsync(db, key, s => s.At > first.At && s.Sections[0].Counters[StatusCounters.Key("acme", "calls")] > calls);
        refreshed.At.ShouldBeGreaterThan(first.At);
        (await db.KeyTimeToLiveAsync(key))!.Value.ShouldBeGreaterThan(TimeSpan.FromSeconds(3), "each write renews the TTL");

        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Hanging_contributor_is_cut_off_and_the_snapshot_is_still_written_within_the_interval()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var service = $"hang-{Guid.NewGuid():N}"[..13];
        var interval = TimeSpan.FromSeconds(1);
        using var host = BuildHost(service, multiplexer, interval, TimeSpan.FromSeconds(5), hanging: true, partTimeout: TimeSpan.FromMilliseconds(300));
        var key = StatusKeys.Snapshot(service, new ServiceIdentity(service).InstanceId);
        var started = DateTime.UtcNow;

        await host.StartAsync(Ct);
        var snapshot = await WaitForAsync(multiplexer.GetDatabase(), key, _ => true);

        (DateTime.UtcNow - started).ShouldBeLessThan(interval, "the hanging part is given up after Status:PartTimeout");
        snapshot.Sections.Select(s => s.Name).ShouldBe(["counting"], "the hanging section (and the failing one) are left out");
        await host.StopAsync(Ct);
    }

    [Theory]
    [InlineData("00:00:10", "00:00:30", "00:00:02", true)]
    [InlineData("00:00:10", "00:00:10", "00:00:01", false)]
    [InlineData("00:00:10", "00:00:30", "00:00:20", false)]
    public void Status_options_need_a_ttl_longer_than_the_interval_plus_the_part_timeout(string interval, string ttl, string part, bool valid)
    {
        var options = new StatusOptions { PublishInterval = TimeSpan.Parse(interval), Ttl = TimeSpan.Parse(ttl), PartTimeout = TimeSpan.Parse(part) };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new(options), results, validateAllProperties: true).ShouldBe(valid);
    }

    [Fact]
    public async Task Snapshot_write_never_throws_when_Redis_is_down()
    {
        var config = ConfigurationOptions.Parse("127.0.0.1:1");
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = 200;
        config.AsyncTimeout = 500;
        await using var dead = await ConnectionMultiplexer.ConnectAsync(config);
        using var host = BuildHost("dead-api", dead, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));

        await host.StartAsync(Ct);
        (await host.Services.GetRequiredService<StatusPublisher>().PublishOnceAsync(Ct)).ShouldBeFalse();
        await Task.Delay(400, Ct);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Breaker_open_is_reported_with_retry_at()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stub:BaseUrl"] = "http://stub.test/",
            ["Stub:ApiKey"] = "key",
            ["Stub:AttemptTimeout"] = "00:00:01",
            ["Stub:RetryCount"] = "1",
            ["Stub:RetryDelay"] = "00:00:00.010",
            ["Stub:TotalTimeout"] = "00:00:05",
            ["Stub:CircuitBreaker:FailureRatio"] = "0.5",
            ["Stub:CircuitBreaker:MinimumThroughput"] = "2",
            ["Stub:CircuitBreaker:SamplingDuration"] = "00:00:30",
            ["Stub:CircuitBreaker:BreakDuration"] = "00:00:20",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalSystemClient<StubClient, StubClient, StubOptions>(configuration, "Stub", "stub", "Stub system");
        services.AddHttpClient("stub").ConfigurePrimaryHttpMessageHandler(() => new FailingHandler());
        await using var provider = services.BuildServiceProvider();
        var states = provider.GetRequiredService<ICircuitBreakerStates>();
        states.Snapshot().ShouldBe([new BreakerDto("Stub system", CircuitState.Closed, null)], "listed before its first call");

        var before = DateTimeOffset.UtcNow;
        var client = provider.GetRequiredService<StubClient>();
        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<Exception>(() => client.CallAsync(Ct));
        }

        var breaker = states.Snapshot().ShouldHaveSingleItem();
        breaker.Name.ShouldBe("Stub system");
        breaker.State.ShouldBe(CircuitState.Open);
        breaker.RetryAt.ShouldNotBeNull();
        breaker.RetryAt.Value.ShouldBeInRange(before.AddSeconds(20), DateTimeOffset.UtcNow.AddSeconds(20));
    }

    [Fact]
    public void Breaker_states_follow_the_reports()
    {
        var states = new CircuitBreakerStates([new KnownCircuitBreaker("B"), new KnownCircuitBreaker("A")]);
        var at = DateTimeOffset.UtcNow.AddSeconds(5);

        states.Report("A", CircuitState.Open, at);
        states.Snapshot().ShouldBe([new BreakerDto("A", CircuitState.Open, at), new BreakerDto("B", CircuitState.Closed, null)]);
        states.Report("A", CircuitState.HalfOpen, at);
        states.Snapshot()[0].ShouldBe(new BreakerDto("A", CircuitState.HalfOpen, null), "only an open circuit has a retry time");
        states.Report("A", CircuitState.Closed, null);
        states.Snapshot()[0].State.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public void Counter_keys_parse_back()
    {
        StatusCounters.Key("acme", "sagas_running").ShouldBe("acme:sagas_running");
        StatusCounters.Key(null, "coalesced_today").ShouldBe("*:coalesced_today");
        StatusCounters.TryParse("acme:sagas_running", out var tenant, out var name).ShouldBeTrue();
        (tenant, name).ShouldBe(("acme", "sagas_running"));
        StatusCounters.TryParse("no-colon", out _, out _).ShouldBeFalse();
        var chaos = new ChaosDto(ChaosMode.Failing, DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        SupplierChaosSection.Read(SupplierChaosSection.From(chaos)).ShouldBe(chaos);
        SupplierChaosSection.Read(SupplierChaosSection.From(new ChaosDto(ChaosMode.Normal, null))).ShouldBe(new ChaosDto(ChaosMode.Normal, null));
    }

    private static async Task<StatusSnapshot> WaitForAsync(IDatabase db, string key, Func<StatusSnapshot, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var value = await db.StringGetAsync(key);
            if (!value.IsNullOrEmpty && JsonSerializer.Deserialize<StatusSnapshot>(value.ToString(), StatusKeys.Json) is { } snapshot && condition(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No matching snapshot at {key}.");
    }

    public sealed class StubOptions : ExternalSystemClientOptions;

    public sealed class StubClient(HttpClient http)
    {
        public async Task CallAsync(CancellationToken cancellationToken)
        {
            using var response = await ExternalSystemClient.SendAsync(
                http, new HttpRequestMessage(HttpMethod.Get, "ping"), "stub", "a ping", (m, _, e) => new InvalidOperationException(m, e), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

/// <summary>The snapshot's message counts include the broker's dead-letter queue, where Wolverine with RabbitMQ really puts them.</summary>
public sealed class WolverineCountsTests(RabbitFixture rabbit) : IClassFixture<RabbitFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed record Doomed(Guid Id);

    public static class DoomedHandler
    {
        public static void Handle(Doomed message) => throw new InvalidOperationException("no error rule matches this");
    }

    [Fact]
    public async Task Wolverine_counts_include_broker_dead_letters()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString })
            .Build();
        var queue = $"doomed-{Guid.NewGuid():N}"[..15];
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString }))
            .UseWolverine(options =>
            {
                options.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DoomedHandler));
                options.UseRabbitMq(new Uri(rabbit.ConnectionString)).AutoProvision();
                options.ListenToRabbitQueue(queue);
                options.PublishMessage<Doomed>().ToRabbitQueue(queue);
            })
            .StartAsync(Ct);
        await using var counts = new WolverineCounts(host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>(), configuration, Microsoft.Extensions.Options.Options.Create(new StatusOptions()), NullLogger<WolverineCounts>.Instance);
        var before = (await counts.FetchAsync(Ct)).BrokerDeadLetters;

        await host.MessageBus().PublishAsync(new Doomed(Guid.NewGuid()));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        WolverineCountsDto now;
        do
        {
            await Task.Delay(100, Ct);
            now = await counts.FetchAsync(Ct);
        }
        while (now.BrokerDeadLetters == before && DateTime.UtcNow < deadline);

        now.BrokerDeadLetters.ShouldBe(before + 1, "the message Wolverine gave up on is in wolverine-dead-letter-queue");
        now.DeadLetters.ShouldBe(0, "no message store here, and the table would not have it anyway");
        await host.StopAsync(Ct);
    }
}
