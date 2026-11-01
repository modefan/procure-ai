using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ProcureAi.DemoData.Tests;

/// <summary>
/// The runner and the seed command against a fake gateway: transient failures are waited out, persistent ones end the seed with exit
/// code 1 and a message naming the request and the step, and a business conflict's reason reaches that message.
/// </summary>
public sealed class ScenarioRunnerTests
{
    private const string Tenant = "acme";
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static Scenario OneRequest(StopAt stopAt) => new(Tenant, "thandi@acme.test",
    [
        new ScenarioRequest("a-key", "A title", "tom@acme.test", "IT-200", "contoso-hardware", [new ScenarioLine("MON-27", 1)], stopAt,
            Approvers: stopAt == StopAt.Draft ? null : ["maria@acme.test"]),
    ]);

    [Fact]
    public async Task Transient_error_while_creating_is_waited_out_and_the_request_created()
    {
        var lookups = 0;
        var gateway = new FakeGateway(request => request switch
        {
            { Method.Method: "GET" } when request.RequestUri!.PathAndQuery.StartsWith("/api/v1/requests?mine=true", StringComparison.Ordinal) =>
                ++lookups == 1 ? FakeGateway.Status(HttpStatusCode.ServiceUnavailable) : FakeGateway.Json(HttpStatusCode.OK, EmptyPage),
            { Method.Method: "POST" } when request.RequestUri!.AbsolutePath == "/api/v1/requests" => FakeGateway.Json(HttpStatusCode.Created, Request("Draft")),
            _ => FakeGateway.Status(HttpStatusCode.NotFound),
        });

        var result = await Runner(gateway).RunAsync(OneRequest(StopAt.Draft), TestContext.Current.CancellationToken);

        result.Failures.Select(f => f.Message).ShouldBeEmpty();
        result.Created.ShouldBe(1);
        lookups.ShouldBe(2, "the 503 was retried");
    }

    [Fact]
    public async Task Persistent_error_while_creating_ends_the_seed_with_exit_code_1_naming_request_and_step()
    {
        var gateway = new FakeGateway(_ => FakeGateway.Status(HttpStatusCode.ServiceUnavailable));
        var directory = Directory.CreateTempSubdirectory("demodata-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, "acme.json"),
                System.Text.Json.JsonSerializer.Serialize(OneRequest(StopAt.Draft), Scenarios.Json),
                TestContext.Current.CancellationToken);
            var logs = new ListLogger();
            using var services = Services(gateway, logs, directory.FullName);

            var exitCode = await new SeedCommand(services).RunAsync(TestContext.Current.CancellationToken);

            exitCode.ShouldBe(1);
            logs.Lines.ShouldContain(l => l.Contains("acme/a-key failed at step 'create'"));

            var result = await services.GetRequiredService<ScenarioRunner>().RunAsync(OneRequest(StopAt.Draft), TestContext.Current.CancellationToken);
            var failure = result.Failures.ShouldHaveSingleItem();
            (failure.Key, failure.Step).ShouldBe(("a-key", "create"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_conflict_on_a_write_names_its_reason_when_the_step_times_out()
    {
        var gateway = new FakeGateway(request => request switch
        {
            { Method.Method: "GET" } when request.RequestUri!.PathAndQuery.StartsWith("/api/v1/requests?mine=true", StringComparison.Ordinal) =>
                FakeGateway.Json(HttpStatusCode.OK, $"{{\"items\":[{Request("Draft")}],\"page\":1,\"pageSize\":100,\"total\":1}}"),
            { Method.Method: "GET" } when request.RequestUri!.AbsolutePath == $"/api/v1/requests/{RequestId}" => FakeGateway.Json(HttpStatusCode.OK, Request("Draft")),
            { Method.Method: "POST" } when request.RequestUri!.AbsolutePath.EndsWith("/submit", StringComparison.Ordinal) =>
                FakeGateway.Json(HttpStatusCode.Conflict, "{\"title\":\"Conflict\",\"detail\":\"Cost center IT-200 is closed for new requests.\"}"),
            _ => FakeGateway.Status(HttpStatusCode.NotFound),
        });

        var result = await Runner(gateway).RunAsync(OneRequest(StopAt.Submitted), TestContext.Current.CancellationToken);

        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Message.ShouldContain("Cost center IT-200 is closed for new requests.");
        failure.Message.ShouldContain("409");
    }

    /// <summary>
    /// Final review F11: a 409 belongs to the write that got it. A later step's write that is accepted and whose wait then times out must
    /// not report the earlier refusal as its reason.
    /// </summary>
    [Fact]
    public async Task A_timeout_after_an_accepted_write_does_not_carry_an_earlier_conflict()
    {
        var submitted = false;
        var gateway = new FakeGateway(request => request switch
        {
            { Method.Method: "GET" } when request.RequestUri!.PathAndQuery.StartsWith("/api/v1/requests?mine=true", StringComparison.Ordinal) =>
                FakeGateway.Json(HttpStatusCode.OK, $"{{\"items\":[{Request("Draft")}],\"page\":1,\"pageSize\":100,\"total\":1}}"),
            { Method.Method: "GET" } when request.RequestUri!.AbsolutePath == $"/api/v1/requests/{RequestId}" =>
                FakeGateway.Json(HttpStatusCode.OK, Request(submitted ? "Submitted" : "Draft")),
            { Method.Method: "GET" } when request.RequestUri!.AbsolutePath == $"/api/v1/approvals/requests/{RequestId}" =>
                FakeGateway.Json(HttpStatusCode.OK, $"{{\"requestId\":\"{RequestId}\",\"state\":\"AwaitingApproval\",\"steps\":[{{\"id\":\"{Guid.NewGuid()}\",\"stepNo\":1,\"level\":\"Manager\",\"status\":\"Pending\",\"canAct\":false,\"cannotActReason\":null,\"version\":1}}]}}"),
            // Someone else submitted it a moment earlier: this submit is refused, but the request moved on as wanted.
            { Method.Method: "POST" } when request.RequestUri!.AbsolutePath.EndsWith("/submit", StringComparison.Ordinal) =>
                Submit(),
            // The cancel is accepted, but the request never shows Cancelled: the step times out.
            { Method.Method: "POST" } when request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal) =>
                FakeGateway.Json(HttpStatusCode.OK, Request("Cancelling")),
            _ => FakeGateway.Status(HttpStatusCode.NotFound),
        });

        HttpResponseMessage Submit()
        {
            submitted = true;
            return FakeGateway.Json(HttpStatusCode.Conflict, "{\"title\":\"Conflict\",\"detail\":\"The request is already submitted.\"}");
        }

        var scenario = new Scenario(Tenant, "thandi@acme.test",
        [
            new ScenarioRequest("a-key", "A title", "tom@acme.test", "IT-200", "contoso-hardware", [new ScenarioLine("MON-27", 1)], StopAt.Cancelled,
                CancelReason: "Not needed"),
        ]);
        var result = await Runner(gateway).RunAsync(scenario, TestContext.Current.CancellationToken);

        var failure = result.Failures.ShouldHaveSingleItem();
        failure.Step.ShouldBe("request cancelled");
        failure.Message.ShouldContain("timed out");
        failure.Message.ShouldNotContain("already submitted", Case.Insensitive);
    }

    [Theory]
    [InlineData("https://localhost:7001", true, true)]
    [InlineData("https://127.0.0.1:7001", true, true)]
    [InlineData("https://[::1]:7001", true, true)]
    [InlineData("https://demo.procure-ai.example", true, false)]
    [InlineData("https://localhost.attacker.example", true, false)]
    [InlineData("https://demo.procure-ai.example", false, true)]
    public void Untrusted_certificates_are_only_allowed_for_a_loopback_gateway(string gatewayUrl, bool allowUntrusted, bool valid)
    {
        var options = Options(TimeSpan.FromSeconds(5));
        options.PollInterval = TimeSpan.FromMilliseconds(500);
        options.GatewayUrl = gatewayUrl;
        options.AllowUntrustedServerCertificate = allowUntrusted;
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true).ShouldBe(valid);
        if (!valid)
        {
            results.ShouldContain(r => r.MemberNames.Contains(nameof(DemoDataOptions.AllowUntrustedServerCertificate)));
        }
    }

    [Theory]
    [InlineData("https://localhost:7001/api", true)]
    [InlineData("https://127.0.0.1:7001/api", true)]
    [InlineData("https://demo.procure-ai.example/api", false)]
    public void The_certificate_check_is_only_skipped_for_loopback(string url, bool skipped) =>
        GatewayCertificates.AcceptsUntrusted(new Uri(url)).ShouldBe(skipped);

    private const string EmptyPage = "{\"items\":[],\"page\":1,\"pageSize\":100,\"total\":0}";

    private static string Request(string status) =>
        $"{{\"id\":\"{RequestId}\",\"number\":\"PR-0001\",\"title\":\"A title\",\"status\":\"{status}\",\"requesterId\":\"00000000-0000-4000-8000-000000000001\",\"costCenterCode\":\"IT-200\",\"version\":1}}";

    private static DemoDataOptions Options(TimeSpan stepTimeout) => new()
    {
        GatewayUrl = "https://localhost:7001",
        AdminApiKey = new string('k', 64),
        StepTimeout = stepTimeout,
        PollInterval = TimeSpan.FromMilliseconds(10),
        MaxParallel = 4,
        ScenarioDirectory = "Scenarios",
    };

    private static ScenarioRunner Runner(FakeGateway gateway)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options(TimeSpan.FromSeconds(1)));
        var client = new GatewayClient(new HttpClient(gateway) { BaseAddress = new Uri("https://localhost:7001") }, options);
        return new ScenarioRunner(client, options, TimeProvider.System, NullLogger<ScenarioRunner>.Instance);
    }

    private static ServiceProvider Services(FakeGateway gateway, ListLogger logs, string scenarioDirectory)
    {
        var services = new ServiceCollection();
        var settings = Options(TimeSpan.FromSeconds(1));
        settings.ScenarioDirectory = scenarioDirectory;
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(settings));
        services.AddSingleton<ILoggerFactory>(new ListLoggerFactory(logs));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new GatewayClient(new HttpClient(gateway) { BaseAddress = new Uri(settings.GatewayUrl) }, sp.GetRequiredService<IOptions<DemoDataOptions>>()));
        services.AddSingleton<ScenarioRunner>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeGateway(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

        public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ListLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }

    private sealed class ListLoggerFactory(ListLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
