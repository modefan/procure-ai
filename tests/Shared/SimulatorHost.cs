extern alias simulators;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ErpStore = simulators::ProcureAi.Simulators.Erp.ErpStore;
using SimulatorProgram = simulators::Program;
using SupplierStore = simulators::ProcureAi.Simulators.Supplier.SupplierStore;

namespace ProcureAi.Testing;

/// <summary>
/// The real supplier and ERP simulator (its Program) on a test server, shared by the integration tests of the services that talk
/// to it (a linked source file: it needs the <c>simulators</c> extern alias of the referencing project, which a library cannot pass
/// on). Its signed webhooks reach the service's test server through <see cref="WebhookTraffic"/>. Real clock, a short confirm delay,
/// no latency, and no automatic invoices unless <paramref name="settings"/> says otherwise (they also name the receiver URLs).
/// </summary>
public sealed class SimulatorHost(WebhookTraffic webhooks, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<SimulatorProgram>
{
    public const string SimulatorKey = "test-simulator-key-0123456789";
    public const string Secret = "test-supplier-webhook-secret-0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var values = new Dictionary<string, string?>
        {
            ["Simulators:ApiKey"] = SimulatorKey,
            ["Auth:AdminApiKeySha256"] = TestAuth.ApiKeySha256,
            ["Simulators:Webhooks:PurchasingUrl"] = "",
            ["Simulators:Webhooks:InvoicingUrl"] = "",
            ["Simulators:Webhooks:Secret"] = Secret,
            ["Simulators:Random:Seed"] = "42",
            ["Simulators:Supplier:ConfirmDelay"] = "00:00:00.300",
            ["Simulators:Supplier:AutoInvoice"] = "false",
            ["Simulators:Chaos:Supplier:MinLatency"] = "00:00:00",
            ["Simulators:Chaos:Supplier:MaxLatency"] = "00:00:00",
            ["Simulators:Chaos:Erp:MinLatency"] = "00:00:00",
            ["Simulators:Chaos:Erp:MaxLatency"] = "00:00:00",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
        builder.ConfigureTestServices(services =>
            services.AddHttpClient("simulator-webhooks").ConfigurePrimaryHttpMessageHandler(() => webhooks));
    }

    public SupplierStore Store => Services.GetRequiredService<SupplierStore>();

    public ErpStore Erp => Services.GetRequiredService<ErpStore>();
}
