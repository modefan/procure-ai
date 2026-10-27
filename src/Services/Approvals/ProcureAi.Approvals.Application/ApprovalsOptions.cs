using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.Approvals.Domain;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Domain;

namespace ProcureAi.Approvals.Application;

/// <summary>Configuration section <c>Approvals</c>. The values live in appsettings; nothing here has a default.</summary>
public sealed class ApprovalsOptions
{
    public const string SectionName = "Approvals";

    [Required, ValidateObjectMembers]
    public ThresholdOptions Thresholds { get; set; } = new();

    [Required, ValidateObjectMembers]
    public TimeoutOptions Timeouts { get; set; } = new();

    /// <summary>How a saga message that lost a race in the database (two messages for one request at once) is retried.</summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new();

    /// <summary>How a saga message is retried when the Access service cannot answer (it is needed to build and escalate the chain).</summary>
    [Required, ValidateObjectMembers]
    public DependencyRetryOptions DependencyRetry { get; set; } = new();

    public ApprovalThresholds ToThresholds() =>
        new(Money.Zar(Thresholds.DepartmentHeadAbove), Money.Zar(Thresholds.FinanceAbove));
}

/// <summary>Totals strictly above these amounts in Rand (incl. VAT) need a department-head / finance step.</summary>
public sealed class ThresholdOptions : IValidatableObject
{
    [Range(typeof(decimal), "0.01", "1000000000")]
    public decimal DepartmentHeadAbove { get; set; }

    [Range(typeof(decimal), "0.01", "1000000000")]
    public decimal FinanceAbove { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FinanceAbove < DepartmentHeadAbove)
        {
            yield return new ValidationResult("FinanceAbove cannot be below DepartmentHeadAbove.", [nameof(FinanceAbove)]);
        }
    }
}

/// <summary>When an open step reminds its approvers, escalates, and (when nobody is above) keeps reminding finance.</summary>
public sealed class TimeoutOptions : IValidatableObject
{
    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan ReminderAfter { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan EscalateAfter { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan FinanceReminderEvery { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EscalateAfter <= ReminderAfter)
        {
            yield return new ValidationResult("EscalateAfter must be later than ReminderAfter.", [nameof(EscalateAfter)]);
        }
    }
}
