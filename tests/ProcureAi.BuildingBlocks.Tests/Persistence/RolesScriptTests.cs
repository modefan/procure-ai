using Npgsql;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Persistence;

/// <summary>
/// infra/postgres/roles.sql (plan 08 ruling 6), run by psql inside the project's image as the AppHost, the chart and the fixtures run it:
/// idempotent, fails loudly without a password, and moves a volume made when every service connected as the superuser (objects owned by
/// postgres, the shared replication role <c>debezium</c>) to the per-service roles.
/// </summary>
public sealed class RolesScriptTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Runs_twice_without_error_and_changes_nothing_the_second_time()
    {
        var first = await postgres.RunRolesScriptAsync();
        var before = await SnapshotAsync();
        var second = await postgres.RunRolesScriptAsync();

        first.ShouldContain("roles.sql: done");
        second.ShouldContain("roles.sql: done");
        second.ShouldNotContain("ERROR", Case.Insensitive);
        (await SnapshotAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Fails_without_a_password_before_touching_that_service()
    {
        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => postgres.RunRolesScriptAsync(PostgresFixture.RolesScriptEnvironment(without: "DB_REQUESTS_CDC_PASSWORD")));

        failure.Message.ShouldContain("no password for role requests_cdc");
        await postgres.RunRolesScriptAsync();
    }

    /// <summary>An empty password variable (set, but to nothing) would clear the role's password with <c>PASSWORD ''</c>: it fails like a missing one.</summary>
    [Theory]
    [InlineData("DB_REQUESTS_PASSWORD", "no password for role requests_app")]
    [InlineData("DB_REQUESTS_CDC_PASSWORD", "no password for role requests_cdc")]
    [InlineData("DB_AUDIT_LOG_READER_PASSWORD", "no password for role audit_log_reader")]
    public async Task Fails_on_an_empty_password(string variable, string expected)
    {
        var environment = PostgresFixture.RolesScriptEnvironment();
        environment[variable] = "";

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => postgres.RunRolesScriptAsync(environment));

        failure.Message.ShouldContain(expected);
        failure.Message.ShouldContain("is not set or empty");
        await postgres.RunRolesScriptAsync();
    }

    /// <summary>
    /// A failing statement is logged with its text (log_min_error_statement=error), which for an ALTER ROLE ... PASSWORD would put the password
    /// into the csvlog Audit's log reader and the collector read. Every session of the scripts turns that off: roles.sql before its first
    /// statement and after its <c>\connect</c>, roles-database.sql after its <c>\connect</c> (roles-pgaudit.sql runs in that session).
    /// </summary>
    [Fact]
    public void Every_session_of_the_scripts_keeps_failing_statements_out_of_the_log()
    {
        const string Set = "SET log_min_error_statement = panic;";
        string[] Lines(string file) =>
            File.ReadAllLines(Path.Combine(PostgresFixture.RepoRoot(), "infra", "postgres", file)).Select(l => l.Trim()).ToArray();

        var roles = Lines("roles.sql");
        var firstStatement = Array.FindIndex(roles, l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal) && !l.StartsWith('\\') &&!l.StartsWith("SET ", StringComparison.Ordinal));
        Array.IndexOf(roles, Set).ShouldBeInRange(0, firstStatement - 1, "roles.sql sets it before any other statement");
        foreach (var file in new[] { "roles.sql", "roles-database.sql", "roles-pgaudit.sql" })
        {
            var lines = Lines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("\\connect", StringComparison.Ordinal) || lines[i].StartsWith("\\c ",StringComparison.Ordinal))
                {
                    lines.Skip(i + 1).Take(3).ShouldContain(Set, $"{file}: the new session of '{lines[i]}' sets it");
                }
            }
        }
    }

    /// <summary>What the setting buys: with it a failing ALTER ROLE ... PASSWORD leaves no STATEMENT line with the password in the server log.</summary>
    [Fact]
    public async Task A_failing_password_statement_does_not_reach_the_server_log()
    {
        var hidden = $"hidden-{Guid.NewGuid():N}";
        var shown = $"shown-{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(postgres.ConnectionString("postgres")) { Pooling = false }.ConnectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("SET log_min_error_statement = panic", connection))
            {
                await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await Should.ThrowAsync<PostgresException>(async () =>
            {
                await using var alter = new NpgsqlCommand($"ALTER ROLE no_such_role_m3 PASSWORD '{hidden}'", connection);
                await alter.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            });
        }

        // The control, after it: without the setting the same failure logs the statement with the password.
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await Should.ThrowAsync<PostgresException>(async () =>
            {
                await using var alter = new NpgsqlCommand($"ALTER ROLE no_such_role_m3 PASSWORD '{shown}'", connection);
                await alter.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            });
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        string log;
        while (!(log = await postgres.ConsoleAsync()).Contains(shown, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        log.ShouldContain(shown, Case.Sensitive, "the control's statement reached the log (so the check below means something)");
        log.ShouldNotContain(hidden, Case.Sensitive);
    }

    /// <summary>
    /// A data volume from before plan 08 Task 4: purchasingdb owned by postgres with tables, a serial and an identity sequence, a free sequence,
    /// a view, a function, an enum, the publication and Wolverine's schema, all owned by postgres, and the old role <c>debezium</c> with its
    /// grants. After roles.sql everything belongs to purchasing_app, purchasing_cdc reads the published tables, and debezium is gone.
    /// </summary>
    [Fact]
    public async Task Moves_an_existing_volume_from_the_superuser_to_the_service_roles()
    {
        var admin = postgres.ConnectionString("postgres");
        await ExecuteAsync(admin, "DROP DATABASE IF EXISTS purchasingdb WITH (FORCE)", "CREATE DATABASE purchasingdb",
            "CREATE ROLE debezium WITH LOGIN REPLICATION PASSWORD 'old'");
        var old = new NpgsqlConnectionStringBuilder(postgres.ConnectionString("purchasingdb")) { Pooling = false }.ConnectionString;
        await ExecuteAsync(old, """
            CREATE TABLE purchase_orders (id serial PRIMARY KEY, title text);
            CREATE TABLE audit_entries (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, entry text);
            CREATE TABLE debezium_heartbeat (id int PRIMARY KEY, beat_at timestamptz NOT NULL);
            INSERT INTO debezium_heartbeat VALUES (1, now());
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY);
            CREATE SCHEMA wolverine; CREATE TABLE wolverine.wolverine_incoming_envelopes (id uuid PRIMARY KEY);
            CREATE SEQUENCE order_numbers; CREATE VIEW open_orders AS SELECT * FROM purchase_orders;
            CREATE FUNCTION next_order_number() RETURNS bigint LANGUAGE sql AS 'SELECT nextval(''order_numbers'')';
            CREATE TYPE order_state AS ENUM ('open', 'closed');
            CREATE PUBLICATION dbz_publication FOR TABLE purchase_orders, audit_entries, debezium_heartbeat;
            GRANT USAGE ON SCHEMA public TO debezium;
            GRANT SELECT ON purchase_orders, audit_entries TO debezium;
            GRANT SELECT, UPDATE ON debezium_heartbeat TO debezium;
            """);
        NpgsqlConnection.ClearAllPools();

        await postgres.RunRolesScriptAsync();
        await postgres.RunRolesScriptAsync();

        (await ListAsync(old, """
            SELECT relname || ':' || pg_get_userbyid(relowner) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('public', 'wolverine') AND c.relkind IN ('r', 'S', 'v') ORDER BY 1
            """)).ShouldAllBe(o => o.EndsWith(":purchasing_app"));
        (await ListAsync(old, """
            SELECT 'schema wolverine:' || pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'wolverine'
            UNION ALL SELECT 'database:' || pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'purchasingdb'
            UNION ALL SELECT 'function:' || pg_get_userbyid(proowner) FROM pg_proc WHERE proname = 'next_order_number'
            UNION ALL SELECT 'type:' || pg_get_userbyid(typowner) FROM pg_type WHERE typname = 'order_state'
            UNION ALL SELECT 'publication:' || pg_get_userbyid(pubowner) FROM pg_publication WHERE pubname = 'dbz_publication'
            ORDER BY 1
            """)).ShouldBe(["database:purchasing_app", "function:purchasing_app", "publication:purchasing_app", "schema wolverine:purchasing_app", "type:purchasing_app"]);
        (await ListAsync(admin, "SELECT rolname FROM pg_roles WHERE rolname = 'debezium'")).ShouldBeEmpty("the old shared role is dropped");

        // The service's role carries on where the superuser left off: migrations alter the publication, the demo reset truncates.
        await ExecuteAsync(postgres.ConnectionStringAs("purchasing_app", "purchasingdb"),
            "CREATE TABLE goods_receipts (id int PRIMARY KEY)", "ALTER PUBLICATION dbz_publication ADD TABLE goods_receipts",
            "TRUNCATE audit_entries", "INSERT INTO purchase_orders (title) VALUES ('after')", "SELECT next_order_number()");
        // The replication role reads what was published before, beats, and nothing else.
        var cdc = postgres.ConnectionStringAs("purchasing_cdc", "purchasingdb");
        await ExecuteAsync(cdc, "SELECT count(*) FROM purchase_orders", "SELECT count(*) FROM audit_entries",
            "UPDATE debezium_heartbeat SET beat_at = now() WHERE id = 1");
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(cdc, "SELECT count(*) FROM \"__EFMigrationsHistory\"")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        await postgres.CreateDatabaseAsync("purchasingdb");
    }

    /// <summary>
    /// The operator's read-only role (plan 08b Task 4 fix round 1 M1, test from fix round 2 m5): with DB_OPERATOR_READER_PASSWORD set,
    /// <c>operator_reader</c> reads every service table, including one the service's role creates after roles.sql ran (in a new schema
    /// too: default privileges), but not the password verifiers in <c>pg_authid</c> (no <c>pg_read_all_data</c>, and an old grant of it
    /// is revoked), and writes nothing. Without the variable the role can no longer log in.
    /// </summary>
    [Fact]
    public async Task The_operator_reader_reads_the_service_tables_but_not_the_password_verifiers()
    {
        var environment = PostgresFixture.RolesScriptEnvironment();
        environment["DB_OPERATOR_READER_PASSWORD"] = PostgresFixture.TestPassword("operator_reader");
        var admin = new NpgsqlConnectionStringBuilder(postgres.ConnectionString("postgres")) { Pooling = false }.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringAs("requests_app", "requestsdb")) { Pooling = false }.ConnectionString;
        var reader = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringAs("operator_reader", "requestsdb")) { Pooling = false }.ConnectionString;
        var readerOnPostgres = new NpgsqlConnectionStringBuilder(postgres.ConnectionStringAs("operator_reader", "postgres")) { Pooling = false }.ConnectionString;
        try
        {
            await postgres.RunRolesScriptAsync(environment);
            // A cluster where fix round 1 had not run yet: the reader held pg_read_all_data. The next run takes it away.
            await ExecuteAsync(admin, "GRANT pg_read_all_data TO operator_reader");
            await postgres.RunRolesScriptAsync(environment);

            // What the service's role makes after roles.sql ran (a migration, Marten or Wolverine at run time), in public and in a new schema.
            await ExecuteAsync(app,
                "CREATE TABLE m5_later (id int PRIMARY KEY, title text)", "INSERT INTO m5_later VALUES (1, 'later')",
                "CREATE SCHEMA m5_later_schema", "CREATE TABLE m5_later_schema.items (id int PRIMARY KEY)", "INSERT INTO m5_later_schema.items VALUES (7)");

            (await ListAsync(admin, """
                SELECT g.rolname FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles u ON u.oid = m.member
                WHERE u.rolname = 'operator_reader'
                """)).ShouldBeEmpty("the reader is a member of no role (pg_read_all_data revoked)");
            (await ListAsync(reader, "SELECT title FROM m5_later")).ShouldBe(["later"]);
            (await ListAsync(reader, "SELECT id::text FROM m5_later_schema.items")).ShouldBe(["7"]);
            (await Should.ThrowAsync<PostgresException>(() => ListAsync(readerOnPostgres, "SELECT rolname FROM pg_authid")))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            (await Should.ThrowAsync<PostgresException>(() => ListAsync(reader, "SELECT rolname FROM pg_authid")))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(reader, "INSERT INTO m5_later VALUES (2, 'no')"));
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(reader, "SET default_transaction_read_only = off; INSERT INTO m5_later VALUES (2, 'no')"));
        }
        finally
        {
            await ExecuteAsync(app, "DROP TABLE IF EXISTS m5_later", "DROP SCHEMA IF EXISTS m5_later_schema CASCADE");
            await postgres.RunRolesScriptAsync();
        }

        (await Should.ThrowAsync<PostgresException>(() => ListAsync(reader, "SELECT 1::text")))
            .SqlState.ShouldBe(PostgresErrorCodes.InvalidAuthorizationSpecification, "without the variable the role cannot log in");
    }

    /// <summary>Roles, their attributes, the service databases' owners and access lists, and every role's table grants in them.</summary>
    private async Task<List<string>> SnapshotAsync()
    {
        var rows = await ListAsync(postgres.ConnectionString("postgres"), """
            SELECT rolname || ' super=' || rolsuper || ' login=' || rolcanlogin || ' repl=' || rolreplication FROM pg_roles
            WHERE rolname LIKE '%\_app' OR rolname LIKE '%\_cdc' OR rolname = 'audit_log_reader'
            UNION ALL
            SELECT datname || ' ' || pg_get_userbyid(datdba) || ' ' || coalesce(datacl::text, '') || ' ' || coalesce(array_to_string(s.setconfig, ','), '')
            FROM pg_database d LEFT JOIN pg_db_role_setting s ON s.setdatabase = d.oid AND s.setrole = 0 WHERE datname LIKE '%db'
            ORDER BY 1
            """);
        foreach (var database in PostgresFixture.ServiceDatabases.Values)
        {
            rows.AddRange(await ListAsync(postgres.ConnectionString(database), """
                SELECT current_database() || ' ' || grantee || ' ' || table_schema || '.' || table_name || ' ' || privilege_type
                FROM information_schema.role_table_grants WHERE grantee LIKE '%\_cdc' OR grantee = 'audit_log_reader' ORDER BY 1
                """));
        }

        return rows;
    }

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<string>> ListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
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
