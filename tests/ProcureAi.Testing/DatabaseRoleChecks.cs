using Npgsql;
using Shouldly;

namespace ProcureAi.Testing;

/// <summary>
/// The least-privilege checks every relational service's integration suite runs on its own migrated database (plan 08 ruling 6), against
/// the roles infra/postgres/roles.sql makes (<see cref="PostgresFixture"/> runs it as deployed):
/// <list type="bullet">
/// <item>the service's role <c>&lt;service&gt;_app</c> is no superuser, owns every object its migration made (so the migration ran as the
/// role, not as the superuser) and is refused by every other service database;</item>
/// <item>it may <c>TRUNCATE</c> its own <c>audit_entries</c> (the demo reset does);</item>
/// <item>the replication role <c>&lt;service&gt;_cdc</c> reads exactly the published tables and updates only the heartbeat row.</item>
/// </list>
/// </summary>
public static class DatabaseRoleChecks
{
    /// <summary>Schemas whose objects stay the superuser's (Audit's file_fdw log tables) besides the system ones.</summary>
    private static readonly string[] SuperuserSchemas = ["pg_catalog", "information_schema", "pg_toast", "pgaudit"];

    /// <summary>
    /// <paramref name="service"/>'s role connects to its own database (<paramref name="connectionString"/>, which must be as that role), owns
    /// every table, sequence, view and function in it outside extensions and the pgaudit schema, is no superuser and may create neither roles
    /// nor databases; every other service database refuses it (no CONNECT).
    /// </summary>
    public static async Task ServiceRoleOwnsItsDatabaseAndCannotReadAnotherAsync(PostgresFixture postgres, string service, string connectionString)
    {
        var role = service + "_app";
        new NpgsqlConnectionStringBuilder(connectionString).Username.ShouldBe(role, "the suite's service connects as its own role");
        var database = PostgresFixture.ServiceDatabases[service];

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            (await ScalarAsync<string>(connection, "SELECT current_user")).ShouldBe(role);
            (await ScalarAsync<bool>(connection, "SELECT rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls FROM pg_roles WHERE rolname = current_user"))
                .ShouldBeFalse($"{role} has no superuser, createrole, createdb, replication or bypassrls");
            (await ScalarAsync<string>(connection, "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()")).ShouldBe(role);
            var foreign = await ListAsync(connection, $"""
                SELECT n.nspname || '.' || c.relname || ' (' || pg_get_userbyid(c.relowner) || ')'
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S') AND c.relowner <> (SELECT oid FROM pg_roles WHERE rolname = current_user)
                  AND n.nspname <> ALL ({Array(SuperuserSchemas)}) AND n.nspname NOT LIKE 'pg\_%'
                  AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
                UNION ALL
                SELECT n.nspname || '.' || p.proname || '() (' || pg_get_userbyid(p.proowner) || ')'
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE p.proowner <> (SELECT oid FROM pg_roles WHERE rolname = current_user)
                  AND n.nspname <> ALL ({Array(SuperuserSchemas)}) AND n.nspname NOT LIKE 'pg\_%'
                  AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
                """);
            foreign.ShouldBeEmpty($"every object of {database} belongs to {role} (its migration ran as {role})");
            (await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pgaudit')"))
                .ShouldBeGreaterThan(0, "the migration made tables");
        }

        foreach (var (other, otherDatabase) in PostgresFixture.ServiceDatabases.Where(kv => kv.Key != service))
        {
            var refused = await Should.ThrowAsync<PostgresException>(async () =>
            {
                await using var connection = new NpgsqlConnection(postgres.ConnectionStringAs(role, otherDatabase));
                await connection.OpenAsync();
            });
            refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} connecting to {otherDatabase}");
            refused.MessageText.ShouldContain($"permission denied for database \"{otherDatabase}\"");
        }
    }

    /// <summary>The service's role may empty its <c>audit_entries</c> (the demo reset's <c>TRUNCATE</c>); rolled back, so no other test notices.</summary>
    public static async Task ServiceRoleCanTruncateItsAuditEntriesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var truncate = new NpgsqlCommand("TRUNCATE TABLE audit_entries", connection, transaction))
        {
            await truncate.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The database's replication role <c>&lt;service&gt;_cdc</c> reads every table of the publication and updates the heartbeat row (as Debezium
    /// does), may not write a captured table and may not read a table outside the publication (<c>__EFMigrationsHistory</c>).
    /// </summary>
    public static async Task CdcRoleReadsOnlyThePublishedTablesAsync(PostgresFixture postgres, string service)
    {
        var database = PostgresFixture.ServiceDatabases[service];
        var (role, _) = PostgresFixture.CdcLoginFor(database);
        role.ShouldBe(service + "_cdc");
        await using var connection = new NpgsqlConnection(postgres.ConnectionStringAs(role, database));
        await connection.OpenAsync();

        (await ScalarAsync<string>(connection, "SHOW procureai.cdc_role")).ShouldBe(role, "the database names its replication role for the migrations");
        var published = await ListAsync(connection, "SELECT format('%I.%I', schemaname, tablename) FROM pg_publication_tables WHERE pubname = 'dbz_publication'");
        published.ShouldContain("public.debezium_heartbeat");
        published.Count.ShouldBeGreaterThan(1);
        foreach (var table in published)
        {
            await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}");
        }

        await ExecuteAsync(connection, "UPDATE debezium_heartbeat SET beat_at = now() WHERE id = 1");
        var captured = published.First(t => t != "public.debezium_heartbeat");
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, $"DELETE FROM {captured}")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} may not write {captured}");
        (await Should.ThrowAsync<PostgresException>(() => ScalarAsync<long>(connection, "SELECT count(*) FROM public.\"__EFMigrationsHistory\"")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{role} may not read a table outside the publication");
    }

    private static string Array(string[] values) => "ARRAY[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ListAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
