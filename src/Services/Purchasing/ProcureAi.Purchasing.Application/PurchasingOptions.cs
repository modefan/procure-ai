using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Purchasing.Application;

/// <summary>Configuration section <c>Purchasing</c>. The values live in appsettings.</summary>
public sealed class PurchasingOptions
{
    public const string SectionName = "Purchasing";

    /// <summary>
    /// The wait before each new attempt to reach the supplier (sending an order, telling it about a cancel): 5 s doubling to
    /// 5 minutes, with jitter. The attempts continue until the supplier answers or the order is cancelled.
    /// </summary>
    [Required, ValidateObjectMembers]
    public BackoffOptions SupplierDelivery { get; set; } = new();

    /// <summary>How a handler that lost a race in the database (webhook against cancel, two receipts) is retried.</summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new();

    /// <summary>How a message is retried when a service it calls cannot answer (Access, for the caller scope).</summary>
    [Required, ValidateObjectMembers]
    public DependencyRetryOptions DependencyRetry { get; set; } = new();
}
