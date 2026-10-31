using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProcureAi.DemoData;

// ProcureAi.DemoData seed | reset | schedule  (plan 06 rulings 1-4)
//   seed      drives the scenario requests of every tenant to their states through the gateway; safe to run again.
//   reset     plan 06 Task 16: clears every service's business data, then seeds.
//   schedule  plan 06 Task 16: runs reset and seed every night at DemoReset:TimeOfDay, Africa/Johannesburg.
// Exit codes: 0 done, 1 a scenario failed (the message names the request and the step), 2 wrong usage.
var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
if (command is not ("seed" or "reset" or "schedule"))
{
    Console.Error.WriteLine("Usage: ProcureAi.DemoData seed | reset | schedule");
    return 2;
}

if (command is not "seed")
{
    Console.Error.WriteLine($"'{command}' comes with the nightly reset (plan 06 Task 16); only 'seed' is available.");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args[1..]);
builder.ConfigureOpenTelemetry();
builder.Services.AddOptions<DemoDataOptions>().Bind(builder.Configuration.GetSection(DemoDataOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ScenarioRunner>();
builder.Services.AddHttpClient<GatewayClient>((services, http) =>
    {
        http.BaseAddress = new Uri(services.GetRequiredService<IOptions<DemoDataOptions>>().Value.GatewayUrl);
        // The resilience handler's own timeouts apply (attempt and total); this one would only cut them short.
        http.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(services =>
    {
        var handler = new HttpClientHandler();
        if (services.GetRequiredService<IOptions<DemoDataOptions>>().Value.AllowUntrustedServerCertificate)
        {
            // Local AppHost only: the gateway serves the ASP.NET Core development certificate (see DemoDataOptions).
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    })
    // Retries 408/429/5xx with backoff. Writes are safe to retry: every one carries its Idempotency-Key.
    .AddStandardResilienceHandler();

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

var exitCode = await new SeedCommand(host.Services).RunAsync(stop.Token);
await host.StopAsync();
return exitCode;
