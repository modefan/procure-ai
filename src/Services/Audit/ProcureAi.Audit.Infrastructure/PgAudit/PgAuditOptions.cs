using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// The <c>PgAudit</c> section: how Audit reads the Postgres server's csvlog (plan 06 ruling 14). Every value comes from appsettings; a
/// missing one stays at its type's default, which the validation refuses.
/// </summary>
public sealed class PgAuditOptions
{
    public const string SectionName = "PgAudit";

    /// <summary>
    /// The connection string (<c>ConnectionStrings:&lt;name&gt;</c>) to <c>auditdb</c> as the role <c>audit_log_reader</c>, the only role that
    /// may read the log. The <c>--migrate</c> run creates that role with this connection string's user name and password.
    /// </summary>
    [Required]
    public string ConnectionName { get; set; } = "";

    /// <summary>
    /// Today's log file, relative to the server's data directory; <c>{0}</c> is the weekday (<c>Mon</c> ... <c>Sun</c>, in the server's
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
}
