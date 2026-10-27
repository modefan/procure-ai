using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Invoicing.Application;

/// <summary>Configuration section <c>Invoicing</c>. The values live in appsettings.</summary>
public sealed class InvoicingOptions
{
    public const string SectionName = "Invoicing";

    /// <summary>How far a line (quantity times price) or the invoice total may differ from the order, in Rand, and still match.</summary>
    [Range(typeof(decimal), "0", "100000")]
    public decimal PriceTolerance { get; set; } = 0.50m;

    /// <summary>
    /// The wait before each new attempt to hand a payment to the ERP: 5 s doubling to 5 minutes, with jitter. The attempts
    /// continue until the ERP takes the payment.
    /// </summary>
    [Required, ValidateObjectMembers]
    public BackoffOptions ErpExport { get; set; } = new();

    /// <summary>How a handler that lost a race in the database (two payments or matches of one order) is retried.</summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new();

    /// <summary>How a message is retried when a service it calls cannot answer (Access, for the caller scope).</summary>
    [Required, ValidateObjectMembers]
    public DependencyRetryOptions DependencyRetry { get; set; } = new();
}
