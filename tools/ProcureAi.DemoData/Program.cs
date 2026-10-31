using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.DemoData;

// ProcureAi.DemoData seed | reset | schedule  (plan 06 rulings 1-4)
//   seed      drives the scenario requests of every tenant to their states through the gateway; safe to run again.
//   reset     clears every service's business data (simulators, the services in parallel, Audit last), then seeds.
//   schedule  runs reset every night at DemoReset:TimeOfDay in DemoReset:TimeZone (02:00 Africa/Johannesburg), until stopped.
// Exit codes: 0 done, 1 a scenario or a reset call failed (the log names it), 2 wrong usage.
var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
if (command is not ("seed" or "reset" or "schedule"))
{
    Console.Error.WriteLine("Usage: ProcureAi.DemoData seed | reset | schedule");
    return 2;
}

const string ResetGateway = "reset-gateway";
const string ResetSimulators = "reset-simulators";

var builder = Host.CreateApplicationBuilder(args[1..]);
builder.ConfigureOpenTelemetry();
builder.Services.AddOptions<DemoDataOptions>().Bind(builder.Configuration.GetSection(DemoDataOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
if (command is not "seed")
{
    builder.Services.AddOptions<DemoDataOptions>()
        .Validate(o => !string.IsNullOrWhiteSpace(o.SimulatorUrl), "DemoData:SimulatorUrl is required for reset and schedule.");
}

builder.Services.AddOptions<DemoResetScheduleOptions>()
    .Bind(builder.Configuration.GetSection(DemoResetScheduleOptions.Section))
    .ValidateDataAnnotations()
    .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "DemoReset:TimeZone is not a known time zone.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScenarioRunner>();
builder.Services.AddHttpClient<GatewayClient>((services, http) =>
    {
        http.BaseAddress = new Uri(services.GetRequiredService<IOptions<DemoDataOptions>>().Value.GatewayUrl);
        // The resilience handler's own timeouts apply (attempt and total); this one would only cut them short.
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CertificateHandler)
    // Retries 408/429/5xx with backoff. Writes are safe to retry: every one carries its Idempotency-Key.
    .AddStandardResilienceHandler();

// The reset's calls: no resilience handler (ResetRunner retries and times out itself; a reset may outlast the standard attempt timeout).
builder.Services.AddHttpClient(ResetGateway, (services, http) =>
    {
        http.BaseAddress = new Uri(services.GetRequiredService<IOptions<DemoDataOptions>>().Value.GatewayUrl);
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CertificateHandler);
builder.Services.AddHttpClient(ResetSimulators, (services, http) =>
    {
        http.BaseAddress = new Uri(services.GetRequiredService<IOptions<DemoDataOptions>>().Value.SimulatorUrl ?? "http://localhost/");
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CertificateHandler);
builder.Services.AddSingleton(services =>
{
    var factory = services.GetRequiredService<IHttpClientFactory>();
    return new ResetClients(factory.CreateClient(ResetGateway), factory.CreateClient(ResetSimulators));
});
builder.Services.AddSingleton<ResetRunner>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProcureAi.DemoData");
try
{
    await host.StartAsync();
}
catch (OptionsValidationException e)
{
    logger.LogError("Invalid DemoData configuration: {Errors}", string.Join(" ", e.Failures));
    return 1;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var exitCode = command switch
{
    "reset" => await new ResetCommand(host.Services).RunAsync(stop.Token),
    "schedule" => await new ScheduleCommand(host.Services, host.Services.GetRequiredService<IOptions<DemoResetScheduleOptions>>().Value).RunAsync(stop.Token),
    _ => await new SeedCommand(host.Services).RunAsync(stop.Token),
};
await host.StopAsync();
return exitCode;

static HttpMessageHandler CertificateHandler(IServiceProvider services)
{
    var handler = new HttpClientHandler();
    if (services.GetRequiredService<IOptions<DemoDataOptions>>().Value.AllowUntrustedServerCertificate)
    {
        // Local AppHost only: the gateway serves the ASP.NET Core development certificate. Options validation refuses the switch for a
        // gateway that is not on loopback, and the check is only skipped for loopback hosts, whatever the request (GatewayCertificates).
        handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None
            || (request.RequestUri is { } uri && GatewayCertificates.AcceptsUntrusted(uri));
    }

    return handler;
}
