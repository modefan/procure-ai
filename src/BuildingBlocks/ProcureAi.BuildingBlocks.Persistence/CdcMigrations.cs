using Microsoft.EntityFrameworkCore.Migrations;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// Change data capture setup for a service's migrations. Debezium Server reads one database through the publication
/// <see cref="PublicationName"/> as the role <see cref="DebeziumRole"/> (created once by the Postgres init script).
/// The migration, not the init script, creates the publication and the grants, so a new service database needs nothing
/// else. The SQL is idempotent: it also runs against a database that already has the publication, tables and grants.
/// </summary>
public static class CdcMigrations
{
    public const string PublicationName = "dbz_publication";
    public const string DebeziumRole = "debezium";

    /// <summary>
    /// Creates the publication in the current database if missing, adds <paramref name="tables"/> (schema <c>public</c>) to it,
    /// sets <c>REPLICA IDENTITY FULL</c> (the complete old row in every update/delete event) and lets <c>debezium</c> read them.
    /// Fails when the <c>debezium</c> role does not exist: a database that is meant to be captured must never silently skip it.
    /// </summary>
    public static MigrationBuilder EnableCdc(this MigrationBuilder migrationBuilder, params string[] tables)
    {
        migrationBuilder.Sql($$"""
            DO $cdc$
            DECLARE t text;
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{DebeziumRole}}') THEN
                    RAISE EXCEPTION 'The role {{DebeziumRole}} does not exist: change data capture cannot be set up (see infra/postgres/initdb).';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = '{{PublicationName}}') THEN
                    CREATE PUBLICATION {{PublicationName}};
                END IF;
                GRANT USAGE ON SCHEMA public TO {{DebeziumRole}};
                FOREACH t IN ARRAY {{TableArray(tables)}} LOOP
                    IF NOT EXISTS (SELECT 1 FROM pg_publication_tables
                                   WHERE pubname = '{{PublicationName}}' AND schemaname = 'public' AND tablename = t) THEN
                        EXECUTE format('ALTER PUBLICATION {{PublicationName}} ADD TABLE public.%I', t);
                    END IF;
                    EXECUTE format('ALTER TABLE public.%I REPLICA IDENTITY FULL', t);
                    EXECUTE format('GRANT SELECT ON public.%I TO {{DebeziumRole}}', t);
                END LOOP;
            END
            $cdc$;
            """);
        return migrationBuilder;
    }

    /// <summary>Reverses <see cref="EnableCdc"/> for <paramref name="tables"/>. The publication itself stays (other tables may use it).</summary>
    public static MigrationBuilder DisableCdc(this MigrationBuilder migrationBuilder, params string[] tables)
    {
        migrationBuilder.Sql($$"""
            DO $cdc$
            DECLARE t text;
            BEGIN
                FOREACH t IN ARRAY {{TableArray(tables)}} LOOP
                    IF EXISTS (SELECT 1 FROM pg_publication_tables
                               WHERE pubname = '{{PublicationName}}' AND schemaname = 'public' AND tablename = t) THEN
                        EXECUTE format('ALTER PUBLICATION {{PublicationName}} DROP TABLE public.%I', t);
                    END IF;
                    EXECUTE format('ALTER TABLE public.%I REPLICA IDENTITY DEFAULT', t);
                END LOOP;
            END
            $cdc$;
            """);
        return migrationBuilder;
    }

    /// <summary>
    /// Drops the logical replication slot <paramref name="slotName"/> of the current database when it exists and no consumer
    /// is connected to it. An unused slot keeps Postgres from recycling WAL, so a slot left behind by a renamed Debezium
    /// instance would grow the disk without limit. An active slot is never touched.
    /// </summary>
    public static MigrationBuilder DropInactiveReplicationSlot(this MigrationBuilder migrationBuilder, string slotName)
    {
        migrationBuilder.Sql(
            "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots " +
            $"WHERE slot_name = '{slotName.Replace("'", "''", StringComparison.Ordinal)}' AND NOT active AND database = current_database();");
        return migrationBuilder;
    }

    private static string TableArray(string[] tables) =>
        "ARRAY[" + string.Join(", ", tables.Select(t => "'" + t.Replace("'", "''", StringComparison.Ordinal) + "'")) + "]::text[]";
}
