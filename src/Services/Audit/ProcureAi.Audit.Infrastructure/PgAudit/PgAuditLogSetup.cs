using Npgsql;

namespace ProcureAi.Audit.Infrastructure.PgAudit;

/// <summary>
/// Makes the Postgres server's csvlog readable from <c>auditdb</c> (plan 06 ruling 14), run by Audit's <c>--migrate</c> as the superuser
/// the migration connects with. Idempotent, so every migrate run re-applies it (and a changed password):
/// <list type="bullet">
/// <item>the role <c>audit_log_reader</c> (user name and password from <c>ConnectionStrings:auditlog</c>), a member of
/// <c>pg_read_server_files</c> (only such a role may point a <c>file_fdw</c> table at a file);</item>
/// <item>the extension <c>file_fdw</c>, the server <c>pgaudit_log</c> and, in schema <c>pgaudit</c>, the foreign table <c>log_today</c> with
/// the csvlog columns of Postgres 17, owned by the reader (it switches the file name every day; see <see cref="PgAuditReader"/>). Nobody
/// else gets any right on the schema or the table.</item>
/// </list>
/// Not an image init script: those run once, on an empty data directory, before Aspire creates <c>auditdb</c>, and never again on an
/// existing volume.
/// </summary>
public static class PgAuditLogSetup
{
    /// <summary>The csvlog columns of Postgres 14 to 17 (backend_type, leader_pid and query_id came with 14).</summary>
    private const string Columns = """
        log_time timestamp(3) with time zone, user_name text, database_name text, process_id integer, connection_from text,
        session_id text, session_line_num bigint, command_tag text, session_start_time timestamp with time zone,
        virtual_transaction_id text, transaction_id bigint, error_severity text, sql_state_code text, message text, detail text,
        hint text, internal_query text, internal_query_pos integer, context text, query text, query_pos integer, location text,
        application_name text, backend_type text, leader_pid integer, query_id bigint
        """;

    public static async Task EnsureAsync(string superuserConnectionString, string readerConnectionString, CancellationToken cancellationToken)
    {
        var reader = new NpgsqlConnectionStringBuilder(readerConnectionString);
        if (string.IsNullOrEmpty(reader.Username) || string.IsNullOrEmpty(reader.Password))
        {
            throw new InvalidOperationException("The audit log connection string needs a user name and a password.");
        }

        await using var connection = new NpgsqlConnection(superuserConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The password goes in as a parameter (set_config) and into the DDL through format('%L'); pgaudit logs the ALTER ROLE with the
        // password replaced by <REDACTED>. The role name is quoted with %I.
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
                EXECUTE format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', reader,
                    current_setting('procureai.audit_log_password'));
                EXECUTE format('GRANT pg_read_server_files TO %I', reader);
                EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), reader);
            END $$;
            """, connection, transaction))
        {
            role.Parameters.AddWithValue("name", reader.Username);
            role.Parameters.AddWithValue("password", reader.Password);
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        var readerRole = QuoteIdentifier(reader.Username);
        await using (var objects = new NpgsqlCommand(
            $"""
            CREATE EXTENSION IF NOT EXISTS file_fdw;
            CREATE SERVER IF NOT EXISTS pgaudit_log FOREIGN DATA WRAPPER file_fdw;
            CREATE SCHEMA IF NOT EXISTS pgaudit;
            REVOKE ALL ON SCHEMA pgaudit FROM PUBLIC;
            GRANT USAGE ON SCHEMA pgaudit TO {readerRole};
            GRANT USAGE ON FOREIGN SERVER pgaudit_log TO {readerRole};
            CREATE FOREIGN TABLE IF NOT EXISTS pgaudit.log_today ({Columns})
                SERVER pgaudit_log OPTIONS (filename 'log/postgresql-Mon.csv', format 'csv');
            ALTER FOREIGN TABLE pgaudit.log_today OWNER TO {readerRole};
            REVOKE ALL ON pgaudit.log_today FROM PUBLIC;
            """, connection, transaction))
        {
            await objects.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
