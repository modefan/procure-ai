using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// The <c>PgAudit</c> section: how Audit reads the Postgres server's csvlog (plan 06 ruling 14). Every value comes from appsettings; a
/// missing one stays at its type's default, which the validation refuses.
/// </summary>
public sealed partial class PgAuditOptions : IValidatableObject
{
    public const string SectionName = "PgAudit";

    /// <summary>
    /// The connection string (<c>ConnectionStrings:&lt;name&gt;</c>) to <c>auditdb</c> as the role <c>audit_log_reader</c>, the only role that
    /// may read the log. The <c>--migrate</c> run creates that role with this connection string's user name and password.
    /// </summary>
    [Required]
    public string ConnectionName { get; set; } = "";

    /// <summary>The schema of the seven weekday tables (<c>pgaudit</c>) in <c>auditdb</c>; a plain lower-case identifier (it goes into SQL).</summary>
    [Required]
    [RegularExpression("^[a-z_][a-z0-9_]{0,62}$", ErrorMessage = "Schema must be a plain lower-case identifier.")]
    public string Schema { get; set; } = "";

    /// <summary>
    /// Each weekday's log file (the <c>--migrate</c> run creates one table per weekday over it), relative to the server's data directory; <c>{0}</c> is the weekday (<c>Mon</c> ... <c>Sun</c>, in the server's
    /// <c>log_timezone</c>), as <c>log_filename=postgresql-%a.log</c> names it (the csvlog gets <c>.csv</c> instead of <c>.log</c>).
    /// </summary>
    [Required]
    [RegularExpression(@"^[A-Za-z0-9_./-]*\{0\}[A-Za-z0-9_./-]*$", ErrorMessage = "LogFileTemplate must hold {0} once and only letters, digits, '_', '.', '/' and '-'.")]
    public string LogFileTemplate { get; set; } = "";

    /// <summary>The summary covers this long back (a day: the log file holds today only).</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan Window { get; set; }

    /// <summary>How many of the newest statements the summary lists.</summary>
    [Range(1, 100)]
    public int LatestCount { get; set; }

    /// <summary>A statement's text is cut to this many characters.</summary>
    [Range(20, 2000)]
    public int TextMaxLength { get; set; }

    /// <summary>
    /// Schemas whose DDL is left out of the newest statements (still counted): <c>wolverine</c>, whose
    /// <c>CREATE TABLE IF NOT EXISTS wolverine.…</c> runs at every service start and would push a person's <c>ALTER</c> out of the list
    /// (final review F16). Plain lower-case identifiers (they go into a pattern). Empty: nothing is left out.
    /// </summary>
    public List<string> IgnoredDdlSchemas { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IgnoredDdlSchemas.Any(s => s is null || !IdentifierPattern().IsMatch(s)))
        {
            yield return new ValidationResult(
                "Every entry of IgnoredDdlSchemas must be a plain lower-case identifier.", [nameof(IgnoredDdlSchemas)]);
        }
    }

    /// <summary>
    /// A POSIX pattern (Postgres <c>~*</c>) matching a statement that names one of <see cref="IgnoredDdlSchemas"/> as a whole word
    /// (<c>wolverine.x</c>, <c>SCHEMA wolverine</c>; not <c>wolverine_x</c>); null when there are none.
    /// </summary>
    public string? IgnoredDdlPattern() =>
        IgnoredDdlSchemas.Count == 0 ? null : $@"\m({string.Join('|', IgnoredDdlSchemas)})\M";

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial System.Text.RegularExpressions.Regex IdentifierPattern();
}
