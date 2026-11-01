using System.Globalization;
using System.Text;
using Npgsql;

namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// Makes the Postgres server's csvlog readable from <c>auditdb</c> (plan 06 ruling 14), run by Audit's <c>--migrate</c> as the superuser
/// the migration connects with. Idempotent, so every migrate run re-applies it (and a changed password):
/// <list type="bullet">
/// <item>the role <c>audit_log_reader</c> (user name and password from <c>ConnectionStrings:auditlog</c>): login, CONNECT on the database,
/// USAGE on the schema and SELECT on the seven log tables, nothing else. It is not a member of <c>pg_read_server_files</c> and owns no
/// table, so it cannot point a table or <c>COPY</c> at another server file (both revoked here from volumes made by an earlier version);</item>
/// <item>the extension <c>file_fdw</c>, the server <c>pgaudit_log</c> and, in the schema (<c>pgaudit</c>), one foreign table per weekday,
/// <c>log_mon</c> ... <c>log_sun</c>, each over that weekday's file, owned by the superuser. Postgres names its files after the weekday
/// (<c>log_filename=postgresql-%a.log</c>), so the reader picks today's table and no file name ever changes at run time.</item>
/// </list>
/// Not an image init script: those run once, on an empty data directory, before Aspire creates <c>auditdb</c>, and never again on an
/// existing volume.
/// </summary>
public static class PgAuditLogSetup
{
    /// <summary>The weekdays as Postgres' strftime <c>%a</c> names them (the file names) and the tables are named (lower case).</summary>
    public static IReadOnlyList<string> Weekdays { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>The csvlog columns of Postgres 14 to 17 (backend_type, leader_pid and query_id came with 14).</summary>
    private const string Columns = """
        log_time timestamp(3) with time zone, user_name text, database_name text, process_id integer, connection_from text,
        session_id text, session_line_num bigint, command_tag text, session_start_time timestamp with time zone,
        virtual_transaction_id text, transaction_id bigint, error_severity text, sql_state_code text, message text, detail text,
        hint text, internal_query text, internal_query_pos integer, context text, query text, query_pos integer, location text,
        application_name text, backend_type text, leader_pid integer, query_id bigint
        """;

    /// <summary>The table of one weekday (<c>Mon</c> -> <c>log_mon</c>).</summary>
    public static string TableName(string weekday) => "log_" + weekday.ToLowerInvariant();

    /// <param name="schema">The schema of the log tables (<see cref="PgAuditOptions.Schema"/>, validated to a plain identifier).</param>
    /// <param name="logFileTemplate">Each weekday's file (<see cref="PgAuditOptions.LogFileTemplate"/>, validated to plain path characters).</param>
    public static async Task EnsureAsync(
        string superuserConnectionString, string readerConnectionString, string schema, string logFileTemplate, CancellationToken cancellationToken)
    {
        var reader = new NpgsqlConnectionStringBuilder(readerConnectionString);
        if (string.IsNullOrEmpty(reader.Username) || string.IsNullOrEmpty(reader.Password))
        {
            throw new InvalidOperationException("The audit log connection string needs a user name and a password.");
        }

        // The setup resets the reader's password and takes pg_read_server_files from its role. Run against the migration user itself, that
        // would change the password the migration (and the app) log in with and strip its own rights. Refuse before touching anything.
        var migrationUser = new NpgsqlConnectionStringBuilder(superuserConnectionString).Username;
        if (string.Equals(migrationUser, reader.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The audit log reader must be a role of its own: its user name equals the user the migration connects as.");
        }

        await using var connection = new NpgsqlConnection(superuserConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The password goes in as a parameter (set_config) and into the DDL through format('%L'); pgaudit logs the ALTER ROLE with the
        // password replaced by <REDACTED>. The role name is quoted with %I. REVOKE of a role it is not a member of only warns.
        await using (var role = new NpgsqlCommand(
            """
            SELECT set_config('procureai.audit_log_reader', @name, true), set_config('procureai.audit_log_password', @password, true);
            DO $$
            DECLARE
                reader text := current_setting('procureai.audit_log_reader');
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = reader) THEN
                    EXECUTE format('CREATE ROLE %I LOGIN', reader);
                END IF;
                EXECUTE format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', reader,
                    current_setting('procureai.audit_log_password'));
                IF pg_has_role(reader, 'pg_read_server_files', 'MEMBER') THEN
                    EXECUTE format('REVOKE pg_read_server_files FROM %I', reader);
                END IF;
                EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), reader);
            END $$;
            """, connection, transaction))
        {
            role.Parameters.AddWithValue("name", reader.Username);
            role.Parameters.AddWithValue("password", reader.Password);
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        var readerRole = QuoteIdentifier(reader.Username);
        var sql = new StringBuilder(
            $"""
            CREATE EXTENSION IF NOT EXISTS file_fdw;
            CREATE SERVER IF NOT EXISTS pgaudit_log FOREIGN DATA WRAPPER file_fdw;
            REVOKE ALL ON FOREIGN SERVER pgaudit_log FROM PUBLIC, {readerRole};
            CREATE SCHEMA IF NOT EXISTS {schema};
            ALTER SCHEMA {schema} OWNER TO CURRENT_USER;
            REVOKE ALL ON SCHEMA {schema} FROM PUBLIC, {readerRole};
            GRANT USAGE ON SCHEMA {schema} TO {readerRole};
            DROP FOREIGN TABLE IF EXISTS {schema}.log_today;

            """);
        foreach (var weekday in Weekdays)
        {
            var table = $"{schema}.{TableName(weekday)}";
            var file = string.Format(CultureInfo.InvariantCulture, logFileTemplate, weekday).Replace("'", "''", StringComparison.Ordinal);
            sql.Append(CultureInfo.InvariantCulture, $"""
                CREATE FOREIGN TABLE IF NOT EXISTS {table} ({Columns}) SERVER pgaudit_log OPTIONS (filename '{file}', format 'csv');
                ALTER FOREIGN TABLE {table} OWNER TO CURRENT_USER;
                ALTER FOREIGN TABLE {table} OPTIONS (SET filename '{file}');
                REVOKE ALL ON {table} FROM PUBLIC, {readerRole};
                GRANT SELECT ON {table} TO {readerRole};

                """);
        }

        await using (var objects = new NpgsqlCommand(sql.ToString(), connection, transaction))
        {
            await objects.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
