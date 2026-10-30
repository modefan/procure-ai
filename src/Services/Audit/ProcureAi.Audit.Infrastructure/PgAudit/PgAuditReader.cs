using System.Globalization;
using System.Text;
using Npgsql;
using ProcureAi.Audit.Application.Abstractions;

namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// Reads today's pgaudit lines through the <c>file_fdw</c> table <c>pgaudit.log_today</c> (created by <see cref="PgAuditLogSetup"/>), as
/// <c>audit_log_reader</c>. Postgres names the log file after the weekday (<c>log_filename=postgresql-%a.log</c>), so one read, in one
/// transaction: compute today's file name on the server (its <c>log_timezone</c>, the zone it names files in), point the table at it
/// when it still points at another day's file (DDL, so once a day; its exclusive lock keeps concurrent readers from seeing a half
/// switch), then count the audit lines of the window per role and class and take the newest statements, in one scan of the file.
/// A file that does not exist yet (after midnight until the first log line) gives an empty summary.
/// </summary>
public sealed class PgAuditReader(string connectionString, PgAuditOptions options, TimeProvider time) : IPgAuditReader
{
    /// <summary>The table the setup creates.</summary>
    public const string Table = "pgaudit.log_today";

    // Today's weekday as strftime's %a names it (to_char's 'Dy' is English, like Postgres' own strftime), in the zone the server logs in,
    // and the file the table reads now.
    private const string FileSql = """
        SELECT to_char(now() AT TIME ZONE current_setting('log_timezone'), 'Dy'),
               (SELECT option_value FROM pg_foreign_table t, pg_options_to_table(t.ftoptions)
                WHERE t.ftrelid = 'pgaudit.log_today'::regclass AND option_name = 'filename')
        """;

    // One scan: 'c' rows are the counts per role and class, 'l' rows the newest audit lines. The first five fields of a pgaudit message
    // (AUDIT: SESSION, statement ID, substatement ID, class, command) never hold a comma; the statement text is parsed in code.
    private const string SummarySql = """
        WITH audit AS MATERIALIZED (
            SELECT log_time, coalesce(user_name, '') AS role, coalesce(database_name, '') AS db, message
            FROM pgaudit.log_today
            WHERE message LIKE 'AUDIT: %' AND log_time >= @from)
        SELECT 'c', role, split_part(message, ',', 4), count(*), NULL::timestamptz, NULL, NULL FROM audit GROUP BY role, split_part(message, ',', 4)
        UNION ALL
        (SELECT 'l', role, NULL, NULL, log_time, db, message FROM audit ORDER BY log_time DESC LIMIT @latest)
        """;

    public async Task<PgAuditSummary> ReadAsync(CancellationToken cancellationToken)
    {
        var from = time.GetUtcNow() - options.Window;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await PointAtTodaysFileAsync(connection, transaction, cancellationToken);
            var summary = await SummariseAsync(connection, transaction, from, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return summary;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedFile)
        {
            // No file for today yet. The transaction is rolled back (the table keeps its old file name until a read finds today's).
            return PgAuditSummary.Empty(from);
        }
    }

    private async Task PointAtTodaysFileAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        string weekday;
        string? current;
        await using (var command = new NpgsqlCommand(FileSql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            weekday = reader.GetString(0);
            current = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var today = string.Format(CultureInfo.InvariantCulture, options.LogFileTemplate, weekday);
        if (today == current)
        {
            return;
        }

        // DDL takes no parameters; the template is validated to plain path characters (PgAuditOptions) and the weekday comes from to_char.
        await using var alter = new NpgsqlCommand(
            $"ALTER FOREIGN TABLE {Table} OPTIONS (SET filename '{today.Replace("'", "''", StringComparison.Ordinal)}')", connection, transaction);
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<PgAuditSummary> SummariseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset from, CancellationToken cancellationToken)
    {
        var counts = new List<PgAuditCount>();
        var latest = new List<PgAuditStatement>();
        await using var command = new NpgsqlCommand(SummarySql, connection, transaction);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("latest", options.LatestCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.GetString(1);
            if (reader.GetString(0) == "c")
            {
                counts.Add(new PgAuditCount(role, reader.GetString(2), reader.GetInt64(3)));
                continue;
            }

            var fields = PgAuditMessage.Fields(reader.GetString(6));
            latest.Add(new PgAuditStatement(
                reader.GetFieldValue<DateTimeOffset>(4),
                role,
                reader.GetString(5),
                Field(fields, PgAuditMessage.Class),
                Field(fields, PgAuditMessage.Command),
                PgAuditMessage.Shorten(Field(fields, PgAuditMessage.Statement), options.TextMaxLength)));
        }

        latest.Sort((a, b) => b.At.CompareTo(a.At));
        counts.Sort((a, b) => (a.Role, a.Class).CompareTo((b.Role, b.Class)));
        return new PgAuditSummary(from, counts, latest);
    }

    private static string Field(IReadOnlyList<string> fields, int index) => index < fields.Count ? fields[index] : "";
}

/// <summary>
/// A pgaudit log message: <c>AUDIT: &lt;type&gt;,&lt;statement id&gt;,&lt;substatement id&gt;,&lt;class&gt;,&lt;command&gt;,&lt;object type&gt;,
/// &lt;object name&gt;,&lt;statement&gt;,&lt;parameter&gt;</c>, comma separated, a field in double quotes when it holds a comma, quote or
/// line break (quotes doubled inside). pgaudit replaces passwords in role statements with <c>&lt;REDACTED&gt;</c> itself.
/// </summary>
public static class PgAuditMessage
{
    public const int Class = 3;
    public const int Command = 4;
    public const int Statement = 7;

    private const string Prefix = "AUDIT: ";

    public static IReadOnlyList<string> Fields(string message)
    {
        var text = message.StartsWith(Prefix, StringComparison.Ordinal) ? message[Prefix.Length..] : message;
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    /// <summary>Whitespace runs (line breaks, indentation) become one space; longer text is cut to <paramref name="max"/> characters, ending in "…".</summary>
    public static string Shorten(string statement, int max)
    {
        var flat = string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : string.Concat(flat.AsSpan(0, max - 1), "…");
    }
}
