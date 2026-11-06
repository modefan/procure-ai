using System.Globalization;
using System.Text;
using Npgsql;
using ProcureAi.Audit.Application.Abstractions;

namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// Reads today's pgaudit lines as <c>audit_log_reader</c>, from the <c>file_fdw</c> table of today's weekday (<c>pgaudit.log_mon</c> ...
/// <c>log_sun</c>, created by <see cref="PgAuditLogSetup"/>). Postgres names the log file after the weekday (<c>log_filename=postgresql-%a.log</c>),
/// so a read asks the server for its weekday (its <c>log_timezone</c>, the zone it names files in), then counts the window's audit lines per
/// role and class and takes the newest statements, in one scan of that one file. No DDL, nothing changes on the server. A file that does
/// not exist yet (after midnight until the first log line) fails only that read and gives an empty summary.
/// </summary>
public sealed class PgAuditReader(string connectionString, PgAuditOptions options, TimeProvider time) : IPgAuditReader
{
    // Today's weekday as strftime's %a names it (to_char's 'Dy' is English, like Postgres' own strftime), in the zone the server logs in.
    private const string WeekdaySql = "SELECT to_char(now() AT TIME ZONE current_setting('log_timezone'), 'Dy')";

    // One scan: 'c' rows are the counts per role and class, 'l' rows the newest audit lines. The first five fields of a pgaudit message
    // (AUDIT: SESSION, statement ID, substatement ID, class, command) never hold a comma; the statement text is parsed in code. DDL naming
    // an ignored schema (@ignored, PgAuditOptions.IgnoredDdlSchemas) is counted but not listed.
    private const string SummarySql = """
        WITH audit AS MATERIALIZED (
            SELECT log_time, coalesce(user_name, '') AS role, coalesce(database_name, '') AS db, message
            FROM {0}
            WHERE message LIKE 'AUDIT: %' AND log_time >= @from)
        SELECT 'c', role, split_part(message, ',', 4), count(*), NULL::timestamptz, NULL, NULL FROM audit GROUP BY role, split_part(message, ',', 4)
        UNION ALL
        (SELECT 'l', role, NULL, NULL, log_time, db, message FROM audit
         WHERE @ignored::text IS NULL OR split_part(message, ',', 4) <> 'DDL' OR message !~* @ignored::text
         ORDER BY log_time DESC LIMIT @latest)
        """;

    public async Task<PgAuditSummary> ReadAsync(CancellationToken cancellationToken)
    {
        var from = time.GetUtcNow() - options.Window;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            return await SummariseAsync(connection, await TodaysTableAsync(connection, cancellationToken), from, cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedFile)
        {
            return PgAuditSummary.Empty(from);
        }
    }

    private async Task<string> TodaysTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(WeekdaySql, connection);
        var weekday = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        // Only the seven known names reach the SQL text (the schema is validated in PgAuditOptions).
        var known = PgAuditLogSetup.Weekdays.FirstOrDefault(d => string.Equals(d, weekday, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The server named an unknown weekday '{weekday}'.");
        return $"{options.Schema}.{PgAuditLogSetup.TableName(known)}";
    }

    private async Task<PgAuditSummary> SummariseAsync(NpgsqlConnection connection, string table, DateTimeOffset from, CancellationToken cancellationToken)
    {
        var counts = new List<PgAuditCount>();
        var latest = new List<PgAuditStatement>();
        await using var command = new NpgsqlCommand(string.Format(CultureInfo.InvariantCulture, SummarySql, table), connection);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("latest", options.LatestCount);
        command.Parameters.Add(new NpgsqlParameter("ignored", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)options.IgnoredDdlPattern() ?? DBNull.Value });
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
                PgAuditMessage.Shorten(Field(fields, PgAuditMessage.Statement), options.TextMaxLength),
                PgAuditMessage.ObjectOf(fields)));
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
    public const int ObjectName = 6;
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

    /// <summary>The longest object name the summary shows: a schema-qualified Postgres identifier (63 bytes each) with quotes.</summary>
    private const int MaxObjectName = 140;

    private static readonly string[] Qualifiers = ["IF NOT EXISTS", "IF EXISTS", "ONLY", "CONCURRENTLY"];

    /// <summary>The pgaudit classes whose object is read from the statement when pgaudit gives none (see <see cref="ObjectOf"/>).</summary>
    public static readonly IReadOnlySet<string> StatementFallbackClasses = new HashSet<string>(StringComparer.Ordinal) { "DDL", "ROLE" };

    /// <summary>
    /// The object a statement names: pgaudit's object name when it gives one, otherwise the identifier right after the command at the
    /// start of the statement (<c>CREATE TABLE IF NOT EXISTS public.x (…)</c> → <c>public.x</c>); session logging leaves the object fields
    /// empty for most DDL. Only that one token, never the rest of the statement, and nothing with a string literal in it. Empty when
    /// there is none.
    /// <para>
    /// The statement fallback is only for the classes <see cref="StatementFallbackClasses"/> (<c>DDL</c>, <c>ROLE</c>): the classes
    /// <c>pgaudit.log=ddl,role</c> logs today, whose commands are always followed by the object's name. For any other class (should
    /// <c>pgaudit.log</c> ever add read, write, function or misc) the word after the command is a column, a setting or a keyword
    /// (<c>SELECT password_hash</c>, <c>SET app.token</c>), so only pgaudit's own object name is shown, or nothing.
    /// </para>
    /// </summary>
    public static string ObjectOf(IReadOnlyList<string> fields)
    {
        if (fields.Count > ObjectName && fields[ObjectName].Length > 0)
        {
            return fields[ObjectName].Length <= MaxObjectName ? fields[ObjectName] : "";
        }

        if (fields.Count <= Statement || fields[Command].Length == 0 || !StatementFallbackClasses.Contains(fields[Class]))
        {
            return "";
        }

        var rest = string.Join(' ', fields[Statement].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!rest.StartsWith(fields[Command] + " ", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        rest = rest[(fields[Command].Length + 1)..];
        foreach (var qualifier in Qualifiers)
        {
            if (rest.StartsWith(qualifier + " ", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[(qualifier.Length + 1)..];
            }
        }

        var end = rest.IndexOfAny([' ', '(', ';', ',']);
        var token = end < 0 ? rest : rest[..end];
        return token.Length is > 0 and <= MaxObjectName && !token.Contains('\'') && !token.Contains('$') ? token : "";
    }

    /// <summary>Whitespace runs (line breaks, indentation) become one space; longer text is cut to <paramref name="max"/> characters, ending in "…".</summary>
    public static string Shorten(string statement, int max)
    {
        var flat = string.Join(' ', statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : string.Concat(flat.AsSpan(0, max - 1), "…");
    }
}
