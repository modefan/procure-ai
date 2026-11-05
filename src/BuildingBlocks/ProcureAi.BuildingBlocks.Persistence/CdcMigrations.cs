using Microsoft.EntityFrameworkCore.Migrations;

namespace ProcureAi.BuildingBlocks.Persistence;

/// <summary>
/// Change data capture setup for a service's migrations. Debezium Server reads one database through the publication
/// <see cref="PublicationName"/> as that database's own replication role (<c>&lt;service&gt;_cdc</c>, plan 08 ruling 6), which
/// infra/postgres/roles.sql creates before any migration runs and names in the database setting <see cref="CdcRoleSetting"/>. The migration
/// (as the service's own role <c>&lt;service&gt;_app</c>, which owns the database and its tables) creates the publication, adds its tables and
/// grants the replication role <c>SELECT</c> on them, so a table a later migration captures is readable by Debezium at once; roles.sql
/// re-applies the grants of every published table on each start. The SQL is idempotent: it also runs against a database that already has
/// the publication, tables and grants.
/// </summary>
public static class CdcMigrations
{
    public const string PublicationName = "dbz_publication";

    /// <summary>
    /// The database setting (<c>ALTER DATABASE … SET procureai.cdc_role = '&lt;service&gt;_cdc'</c>, made by infra/postgres/roles.sql and by the
    /// test fixtures) that names the replication role of the current database. Every new session sees it.
    /// </summary>
    public const string CdcRoleSetting = "procureai.cdc_role";

    /// <summary>
    /// PL/pgSQL that sets <c>cdc_role</c> (declared by the caller) to the database's replication role, or fails: a database that is meant to be
    /// captured must never silently skip it.
    /// </summary>
    private const string ResolveCdcRole = $$"""
                cdc_role := coalesce(current_setting('{{CdcRoleSetting}}', true), '');
                IF cdc_role = '' OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = cdc_role AND rolreplication) THEN
                    RAISE EXCEPTION 'No replication role for database %: % is "%" (run infra/postgres/roles.sql first); change data capture cannot be set up.',
                        current_database(), '{{CdcRoleSetting}}', cdc_role;
                END IF;
    """;

    /// <summary>
    /// Creates the publication in the current database if missing, adds <paramref name="tables"/> (schema <c>public</c>) to it,
    /// sets <c>REPLICA IDENTITY FULL</c> (the complete old row in every update/delete event) and lets the database's replication role
    /// (<see cref="CdcRoleSetting"/>) read them. Fails when the database has no replication role.
    /// </summary>
    public static MigrationBuilder EnableCdc(this MigrationBuilder migrationBuilder, params string[] tables)
    {
        migrationBuilder.Sql($$"""
            DO $cdc$
            DECLARE t text;
            DECLARE cdc_role text;
            BEGIN
            {{ResolveCdcRole}}
                IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = '{{PublicationName}}') THEN
                    CREATE PUBLICATION {{PublicationName}};
                END IF;
                EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', cdc_role);
                FOREACH t IN ARRAY {{TableArray(tables)}} LOOP
                    IF NOT EXISTS (SELECT 1 FROM pg_publication_tables
                                   WHERE pubname = '{{PublicationName}}' AND schemaname = 'public' AND tablename = t) THEN
                        EXECUTE format('ALTER PUBLICATION {{PublicationName}} ADD TABLE public.%I', t);
                    END IF;
                    EXECUTE format('ALTER TABLE public.%I REPLICA IDENTITY FULL', t);
                    EXECUTE format('GRANT SELECT ON public.%I TO %I', t, cdc_role);
                END LOOP;
            END
            $cdc$;
            """);
        return migrationBuilder;
    }

    /// <summary>
    /// The table Debezium's heartbeat updates (<c>heartbeat.action.query</c> in infra/debezium/application.properties, the AppHost and the
    /// chart): one row, <c>id = 1</c>. Audit ignores its changes.
    /// </summary>
    public const string HeartbeatTable = "debezium_heartbeat";

    /// <summary>
    /// Creates <see cref="HeartbeatTable"/> with its one row, adds it to the publication and lets the database's replication role update it. Debezium's
    /// heartbeat updates that row every interval; because the table is in the publication, even a database whose captured tables never
    /// change sends Debezium a change, Debezium confirms its LSN and Postgres can recycle the WAL behind the replication slot (Postgres 15+
    /// skips empty transactions, so an update of an unpublished table would not move the slot). Idempotent; run after
    /// <see cref="EnableCdc"/> (it needs the publication and the role).
    /// </summary>
    public static MigrationBuilder EnableHeartbeat(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($$"""
            DO $cdc$
            DECLARE cdc_role text;
            BEGIN
            {{ResolveCdcRole}}
                IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = '{{PublicationName}}') THEN
                    CREATE PUBLICATION {{PublicationName}};
                END IF;
                CREATE TABLE IF NOT EXISTS public.{{HeartbeatTable}} (id int PRIMARY KEY, beat_at timestamptz NOT NULL);
                INSERT INTO public.{{HeartbeatTable}} (id, beat_at) VALUES (1, now()) ON CONFLICT (id) DO NOTHING;
                IF NOT EXISTS (SELECT 1 FROM pg_publication_tables
                               WHERE pubname = '{{PublicationName}}' AND schemaname = 'public' AND tablename = '{{HeartbeatTable}}') THEN
                    ALTER PUBLICATION {{PublicationName}} ADD TABLE public.{{HeartbeatTable}};
                END IF;
                EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', cdc_role);
                EXECUTE format('GRANT SELECT, UPDATE ON public.{{HeartbeatTable}} TO %I', cdc_role);
            END
            $cdc$;
            """);
        return migrationBuilder;
    }

    /// <summary>Reverses <see cref="EnableHeartbeat"/>: the table leaves the publication and is dropped.</summary>
    public static MigrationBuilder DisableHeartbeat(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($$"""
            DO $cdc$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_publication_tables
                           WHERE pubname = '{{PublicationName}}' AND schemaname = 'public' AND tablename = '{{HeartbeatTable}}') THEN
                    ALTER PUBLICATION {{PublicationName}} DROP TABLE public.{{HeartbeatTable}};
                END IF;
                DROP TABLE IF EXISTS public.{{HeartbeatTable}};
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
