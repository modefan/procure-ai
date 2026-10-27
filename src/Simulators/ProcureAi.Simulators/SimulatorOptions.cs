using System.ComponentModel.DataAnnotations;
using ProcureAi.Simulators.Chaos;

namespace ProcureAi.Simulators;

/// <summary>Settings of the simulators (section <c>Simulators</c>).</summary>
public sealed class SimulatorOptions
{
    public const string Section = "Simulators";

    /// <summary>The key the services present in <c>X-Api-Key</c> on <c>supplier/</c> and <c>erp/</c>. Required; comes from a secret.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; set; } = "";

    public WebhookOptions Webhooks { get; set; } = new();

    public RandomOptions Random { get; set; } = new();

    public SupplierOptions Supplier { get; set; } = new();

    public ChaosConfiguration Chaos { get; set; } = new();

    public sealed class WebhookOptions
    {
        /// <summary>Where the supplier's order webhooks go (Purchasing). Empty: those webhooks are dropped with an error in the log.</summary>
        public string PurchasingUrl { get; set; } = "";

        /// <summary>Where the supplier's invoice webhooks go (Invoicing). Empty: those webhooks are dropped with an error in the log.</summary>
        public string InvoicingUrl { get; set; } = "";

        /// <summary>HMAC secret of the signature. Empty: webhooks are dropped with an error in the log (never sent unsigned).</summary>
        public string Secret { get; set; } = "";

        /// <summary>
        /// Waits before each retry of a failed delivery; after the last one the webhook is dropped. The values (1 s, 5 s, 30 s) are in
        /// appsettings.json, not here: the configuration binder appends to an array that already has items instead of replacing them.
        /// </summary>
        public TimeSpan[] RetryDelays { get; set; } = [];

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    }

    public sealed class RandomOptions
    {
        /// <summary>Makes latency and failures repeatable (tests). Unset: random.</summary>
        public int? Seed { get; set; }
    }

    public sealed class SupplierOptions
    {
        public TimeSpan ConfirmDelay { get; set; } = TimeSpan.FromSeconds(3);

        public TimeSpan InvoiceDelay { get; set; } = TimeSpan.FromSeconds(20);

        public bool AutoInvoice { get; set; } = true;

        /// <summary>An order with an article starting with this is declined.</summary>
        public string RejectPrefix { get; set; } = "DISC-";

        public int DeliveryDays { get; set; } = 5;

        public decimal VatRate { get; set; } = 0.15m;

        public string SupplierCode { get; set; } = "SIMSUP";
    }
}

/// <summary>The SHA-256 (hex) of the one admin API key, like every service has (<c>Auth:AdminApiKeySha256</c>).</summary>
public sealed class AdminAuthOptions
{
    public const string Section = "Auth";

    [RegularExpression(@"^([0-9a-fA-F]{64})?\z", ErrorMessage = "AdminApiKeySha256 must be 64 hexadecimal characters or empty.")]
    public string AdminApiKeySha256 { get; set; } = "";
}
