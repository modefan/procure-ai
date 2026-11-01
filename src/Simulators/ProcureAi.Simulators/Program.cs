using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProcureAi.Simulators;
using ProcureAi.Simulators.Chaos;
using ProcureAi.Simulators.Erp;
using ProcureAi.Simulators.Supplier;
using ProcureAi.Simulators.Webhooks;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Configuration -> options, checked at startup (no key, no start). The webhook URLs and secret are checked when a webhook is sent
// instead, so the simulators can run (and be probed) before Purchasing and Invoicing are wired in.
builder.Services.AddOptions<SimulatorOptions>()
    .BindConfiguration(SimulatorOptions.Section)
    .ValidateDataAnnotations()
    .Validate(options => !options.Chaos.Validate().Any(), "Simulators:Chaos is invalid (failure rate 0..1, latency min <= max <= 30 s).")
    .ValidateOnStart();
builder.Services.AddOptions<ChaosRestoreOptions>()
    .BindConfiguration(ChaosRestoreOptions.Section)
    .Validate(options => options.AutoRestoreAfter > TimeSpan.Zero, "Simulators:Chaos:AutoRestoreAfter must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<AdminAuthOptions>()
    .BindConfiguration(AdminAuthOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SimulatorDemoResetOptions>()
    .BindConfiguration(SimulatorDemoResetOptions.Section)
    .ValidateOnStart();

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChaosState>();
builder.Services.AddSingleton<SupplierStore>();
builder.Services.AddSingleton<SupplierService>();
builder.Services.AddSingleton<ErpStore>();
builder.Services.AddSingleton<WebhookQueue>();
builder.Services.AddHostedService<WebhookSender>();

// The sender owns retries and the give-up rule, so the standard resilience handler ServiceDefaults puts on every client is removed here.
builder.Services.AddHttpClient(WebhookSender.ClientName, (services, client) =>
        client.Timeout = services.GetRequiredService<IOptions<SimulatorOptions>>().Value.Webhooks.Timeout)
    .RemoveAllResilienceHandlers();

builder.Services.AddControllers()
    .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// The nightly demo reset exists only where DemoReset:Enabled is true (plan 06 ruling 4), else 404. Decided once the configuration is final
// (after Build, so test and deployment overrides count); the controllers are discovered when MapControllers' endpoints are first built.
app.Services.GetRequiredService<ApplicationPartManager>().FeatureProviders.Add(
    new DemoResetControllerSwitch(app.Services.GetRequiredService<IOptions<SimulatorDemoResetOptions>>().Value.Enabled));

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();

/// <summary>Lets the tests start the real application through WebApplicationFactory.</summary>
public partial class Program;
