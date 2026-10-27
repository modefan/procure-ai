using ProcureAi.BuildingBlocks.Web.Http;

namespace ProcureAi.Purchasing.Infrastructure.Supplier;

/// <summary>
/// The <c>SupplierClient</c> section: where the supplier API is, the key we present to it, and the resilience pipeline of every
/// call (the shared shape of <see cref="ExternalSystemClientOptions"/>). The values live in appsettings; the key comes from a secret.
/// </summary>
public sealed class SupplierClientOptions : ExternalSystemClientOptions
{
    public const string SectionName = "SupplierClient";
}
