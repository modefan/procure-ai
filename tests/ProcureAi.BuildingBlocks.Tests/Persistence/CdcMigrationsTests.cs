using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using ProcureAi.BuildingBlocks.Persistence;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ProcureAi.BuildingBlocks.Tests.Persistence;

/// <summary>The SQL of <see cref="CdcMigrations"/>, run against real Postgres (the project's image: it has the debezium role).</summary>
public sealed class CdcMigrationsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string PlainPostgresImage = "pgvector/pgvector:pg17";

    private static string[] Sql(Action<MigrationBuilder> build)
    {
        var builder = new MigrationBuilder(Provider);
        build(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToArray();
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

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task<string> NewDatabaseWithTablesAsync(string name)
    {
        var connectionString = await postgres.CreateDatabaseAsync(name);
        await ExecuteAsync(connectionString, "CREATE TABLE orders (id int PRIMARY KEY); CREATE TABLE order_lines (id int PRIMARY KEY);");
        return connectionString;
    }

    [Fact]
    public async Task Creates_publication_and_grants_in_a_fresh_database()
    {
        var db = await NewDatabaseWithTablesAsync("cdc_fresh");

        await ExecuteAsync(db, Sql(b => b.EnableCdc("orders", "order_lines")));

        (await QueryAsync(db, "SELECT tablename FROM pg_publication_tables WHERE pubname = 'dbz_publication' ORDER BY 1"))
            .ShouldBe(["order_lines", "orders"]);
        (await QueryAsync(db, "SELECT relreplident::text FROM pg_class WHERE relname IN ('orders', 'order_lines')"))
            .ShouldAllBe(r => r == "f");
        (await QueryAsync(db, "SELECT has_table_privilege('debezium', 'public.orders', 'SELECT')::text")).ShouldBe(["true"]);
        (await QueryAsync(db, "SELECT has_table_privilege('debezium', 'public.order_lines', 'SELECT')::text")).ShouldBe(["true"]);
        (await QueryAsync(db, "SELECT has_schema_privilege('debezium', 'public', 'USAGE')::text")).ShouldBe(["true"]);
    }

    [Fact]
    public async Task Is_idempotent_when_run_twice()
    {
        var db = await NewDatabaseWithTablesAsync("cdc_twice");
        var statements = Sql(b => b.EnableCdc("orders", "order_lines"));

        await ExecuteAsync(db, statements);
        await ExecuteAsync(db, statements);

        (await QueryAsync(db, "SELECT tablename FROM pg_publication_tables WHERE pubname = 'dbz_publication' ORDER BY 1"))
            .ShouldBe(["order_lines", "orders"]);
    }

    [Fact]
    public async Task Works_against_an_existing_publication_with_tables_and_grants()
    {
        var db = await NewDatabaseWithTablesAsync("cdc_existing");
        await ExecuteAsync(
            db,
            "CREATE PUBLICATION dbz_publication FOR TABLE orders;",
            "GRANT USAGE ON SCHEMA public TO debezium;",
            "GRANT SELECT ON orders TO debezium;");

        await ExecuteAsync(db, Sql(b => b.EnableCdc("orders", "order_lines")));

        (await QueryAsync(db, "SELECT tablename FROM pg_publication_tables WHERE pubname = 'dbz_publication' ORDER BY 1"))
            .ShouldBe(["order_lines", "orders"]);
    }

    [Fact]
    public async Task Disable_removes_tables_from_the_publication()
    {
        var db = await NewDatabaseWithTablesAsync("cdc_disable");
        await ExecuteAsync(db, Sql(b => b.EnableCdc("orders", "order_lines")));

        await ExecuteAsync(db, Sql(b => b.DisableCdc("order_lines")));

        (await QueryAsync(db, "SELECT tablename FROM pg_publication_tables WHERE pubname = 'dbz_publication'")).ShouldBe(["orders"]);
        (await QueryAsync(db, "SELECT relreplident::text FROM pg_class WHERE relname = 'order_lines'")).ShouldBe(["d"]);
    }

    [Fact]
    public async Task Fails_without_debezium_role()
    {
        // A plain server: no init script, so no debezium role.
        await using var plain = new PostgreSqlBuilder(PlainPostgresImage).Build();
        await plain.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = plain.GetConnectionString();
        await ExecuteAsync(connectionString, "CREATE TABLE orders (id int PRIMARY KEY);");

        var failure = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, Sql(b => b.EnableCdc("orders"))));

        failure.MessageText.ShouldContain("debezium");
    }
}
