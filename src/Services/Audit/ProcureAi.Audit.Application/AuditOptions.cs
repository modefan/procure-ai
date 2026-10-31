using System.ComponentModel.DataAnnotations;

namespace ProcureAi.Audit.Application;

/// <summary>
/// The <c>Audit</c> section. Every value comes from appsettings (no defaults in code): an option that is missing stays at its
/// type's default, which the validation refuses, so the service fails at startup instead of running on a value nobody chose.
/// </summary>
public sealed class AuditOptions : IValidatableObject
{
    public const string SectionName = "Audit";

    /// <summary>
    /// How long the history waits for display names from Access (cached; on a miss one call per user, in parallel). When it
    /// runs out or Access fails, the history shows user IDs instead: names are decoration, the history itself must answer.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan DisplayNameTimeout { get; set; }

    /// <summary>How Debezium's changes are matched to the app's interceptor rows (<c>Audit:Matching</c>).</summary>
    [Required]
    public MatchingOptions Matching { get; set; } = new();

    /// <summary>Data changes, interceptor rows and alerts older than this many days are purged (spec: 90).</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; }

    /// <summary>How often the purge runs (daily).</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan PurgeInterval { get; set; }

    /// <summary>How many documents of one kind one purge statement deletes; the purge repeats until nothing is left.</summary>
    [Range(1, 100_000)]
    public int PurgeBatchSize { get; set; }

    /// <summary>What Audit knows about the captured tables (<c>Audit:Cdc</c>).</summary>
    [Required]
    public CdcOptions Cdc { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // DataAnnotations does not descend into nested objects by itself.
        foreach (var (name, nested) in new (string, object)[] { (nameof(Matching), Matching), (nameof(Cdc), Cdc) })
        {
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(nested, new ValidationContext(nested), results, validateAllProperties: true);
            foreach (var result in results)
            {
                yield return new ValidationResult($"{name}: {result.ErrorMessage}", [.. result.MemberNames.Select(m => $"{name}.{m}")]);
            }
        }
    }
}

/// <summary><c>Audit:Matching</c>.</summary>
public sealed class MatchingOptions
{
    /// <summary>
    /// A change and an interceptor row with the same transaction ID match only when the commit and the save are at most this far
    /// apart: the transaction ID is Postgres' 32-bit xid and repeats after about 4 billion transactions.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; set; }

    /// <summary>A change still unmatched this long after it arrived is "changed outside the app".</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Grace { get; set; }
}

/// <summary>
/// <c>Audit:Cdc</c>. Debezium's messages carry the row but not which of its columns are the key, so the key columns of the captured
/// tables are configured here (in key order, as the services' EF models declare them; a test keeps both equal).
/// </summary>
public sealed class CdcOptions : IValidatableObject
{
    /// <summary>The key columns of every table not listed in <see cref="KeyColumns"/> (<c>["id"]</c>).</summary>
    [Required, MinLength(1)]
    public string[] DefaultKeyColumns { get; set; } = [];

    /// <summary>Tables whose key is not <see cref="DefaultKeyColumns"/>, by table name.</summary>
    [Required]
    public Dictionary<string, string[]> KeyColumns { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Tables whose key is a purchase request ID (<c>purchase_requests</c>); other tables name it in a <c>request_id</c> column.</summary>
    [Required]
    public string[] RequestTables { get; set; } = [];

    /// <summary>
    /// MongoDB collections (<c>&lt;database&gt;.&lt;collection&gt;</c>) whose values are never stored: their changes keep the field names
    /// only, every value is <c>***</c> (the assistant's conversations, plan 06 ruling 16).
    /// </summary>
    [Required]
    public string[] MaskedCollections { get; set; } = [];

    /// <summary>
    /// A MongoDB change is the app's when the <c>_audit.at</c> it wrote is at most this far from the change's cluster time (and, for a
    /// delete, when the document's "deleting" stamp is). Covers the time between the app's clock and the write, and clock skew.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan MongoStampWindow { get; set; }

    public bool IsMasked(string database, string collection) =>
        MaskedCollections.Contains($"{database}.{collection}", StringComparer.Ordinal);

    public string[] KeyColumnsOf(string table) => KeyColumns.TryGetValue(table, out var columns) ? columns : DefaultKeyColumns;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (table, columns) in KeyColumns)
        {
            if (columns is null || columns.Length == 0)
            {
                yield return new ValidationResult($"KeyColumns of '{table}' is empty.", [nameof(KeyColumns)]);
            }
        }
    }
}
