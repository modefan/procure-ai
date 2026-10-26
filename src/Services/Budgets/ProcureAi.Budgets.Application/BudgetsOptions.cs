using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Budgets.Application;

/// <summary>Configuration section <c>Budgets</c>.</summary>
public sealed class BudgetsOptions : IValidatableObject
{
    public const string SectionName = "Budgets";

    /// <summary>Time zone in which the fiscal year starts (1 March, local time). An IANA id such as Africa/Johannesburg.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>
    /// How a handler that lost a race in the database (a concurrent reservation on the same budget, or a reserve and a release
    /// of one request) is retried. Set in appsettings (<c>Budgets:ConcurrencyRetry</c>).
    /// </summary>
    [Required, ValidateObjectMembers]
    public ConcurrencyRetryOptions ConcurrencyRetry { get; set; } = new();

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(TimeZone))
        {
            yield break;
        }

        var known = true;
        try
        {
            _ = Zone;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            known = false;
        }

        if (!known)
        {
            yield return new ValidationResult($"Unknown time zone '{TimeZone}'.", [nameof(TimeZone)]);
        }
    }
}
