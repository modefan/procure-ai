-- One service database and its roles; included by roles.sql (see there) with the psql variables
--   service       e.g. requests  (roles requests_app and requests_cdc)
--   database      e.g. requestsdb
--   capture       true when a Debezium Server reads the database (then the role <service>_cdc and cdc_password are needed)
--   app_password  the password of <service>_app (from DB_<SERVICE>_PASSWORD), cdc_password the one of <service>_cdc
-- Ends connected to the database.

\set app_role :service '_app'
\set cdc_role :service '_cdc'

-- A password variable that is unset, or set but empty (PASSWORD '' would silently clear the password), fails the run.
\if :{?app_password}
SELECT :'app_password' = '' AS app_password_empty \gset
\else
\set app_password_empty true
\endif
\if :app_password_empty
SELECT set_config('procureai.missing', :'app_role', false) \gset
DO $missing$ BEGIN RAISE EXCEPTION 'roles.sql: no password for role % (its environment variable DB_<SERVICE>_PASSWORD is not set or empty)', current_setting('procureai.missing'); END $missing$;
\endif
\if :capture
\if :{?cdc_password}
SELECT :'cdc_password' = '' AS cdc_password_empty \gset
\else
\set cdc_password_empty true
\endif
\if :cdc_password_empty
SELECT set_config('procureai.missing', :'cdc_role', false) \gset
DO $missing$ BEGIN RAISE EXCEPTION 'roles.sql: no password for role % (its environment variable DB_<SERVICE>_CDC_PASSWORD is not set or empty)', current_setting('procureai.missing'); END $missing$;
\endif
\endif

-- The service's own role: logs in, owns its database, nothing else.
SELECT format('CREATE ROLE %I', :'app_role') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'app_role') \gexec
ALTER ROLE :"app_role" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT PASSWORD :'app_password';

-- Its database (the AppHost's AddDatabase or an earlier run may have made it already), owned by the role, closed to everyone else.
SELECT format('CREATE DATABASE %I OWNER %I', :'database', :'app_role') WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'database') \gexec
ALTER DATABASE :"database" OWNER TO :"app_role";
REVOKE ALL ON DATABASE :"database" FROM PUBLIC;

\if :capture
-- The replication role of the database's one Debezium Server.
SELECT format('CREATE ROLE %I', :'cdc_role') WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = :'cdc_role') \gexec
ALTER ROLE :"cdc_role" WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT PASSWORD :'cdc_password';
GRANT CONNECT ON DATABASE :"database" TO :"cdc_role";
-- Read by CdcMigrations (EnableCdc, EnableHeartbeat) in every new session: the role a migration grants its captured tables to.
ALTER DATABASE :"database" SET procureai.cdc_role = :'cdc_role';
\else
ALTER DATABASE :"database" RESET procureai.cdc_role;
\endif

\connect :"database"
SET client_min_messages = warning;
-- A new session: never log the text of a failing statement (an ALTER ROLE ... PASSWORD in plain text; see roles.sql).
SET log_min_error_statement = panic;
SELECT set_config('procureai.app_role', :'app_role', false), set_config('procureai.cdc_role', CASE WHEN :'capture' THEN :'cdc_role' ELSE '' END, false) \gset

-- Ownership from the superuser to the service's role (a volume made before this script; a no-op afterwards). Left alone: the system
-- schemas, the pgaudit schema (Audit's log tables stay the superuser's, roles-pgaudit.sql), extension members (vector's types and
-- functions), and sequences and indexes that belong to a table (they follow it).
DO $owners$
DECLARE
    app text := current_setting('procureai.app_role');
    kept text[] := ARRAY['pg_catalog', 'information_schema', 'pg_toast', 'pgaudit'];
    r record;
BEGIN
    FOR r IN
        SELECT n.oid, n.nspname FROM pg_namespace n JOIN pg_roles o ON o.oid = n.nspowner
        WHERE o.rolsuper AND n.nspname <> ALL (kept) AND n.nspname NOT LIKE 'pg\_%'
          AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_namespace'::regclass AND d.objid = n.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('ALTER SCHEMA %I OWNER TO %I', r.nspname, app);
    END LOOP;

    FOR r IN
        SELECT c.oid::regclass AS name, c.relkind FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_roles o ON o.oid = c.relowner
        WHERE o.rolsuper AND n.nspname <> ALL (kept) AND n.nspname NOT LIKE 'pg\_%'
          AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f', 'c')
          AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('e', 'a', 'i'))
    LOOP
        EXECUTE format(
            CASE r.relkind
                WHEN 'S' THEN 'ALTER SEQUENCE %s OWNER TO %I'
                WHEN 'v' THEN 'ALTER VIEW %s OWNER TO %I'
                WHEN 'm' THEN 'ALTER MATERIALIZED VIEW %s OWNER TO %I'
                WHEN 'f' THEN 'ALTER FOREIGN TABLE %s OWNER TO %I'
                WHEN 'c' THEN 'ALTER TYPE %s OWNER TO %I'
                ELSE 'ALTER TABLE %s OWNER TO %I'
            END, r.name, app);
    END LOOP;

    FOR r IN
        SELECT p.oid::regprocedure AS name FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_roles o ON o.oid = p.proowner
        WHERE o.rolsuper AND n.nspname <> ALL (kept) AND n.nspname NOT LIKE 'pg\_%'
          AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format('ALTER ROUTINE %s OWNER TO %I', r.name, app);
    END LOOP;

    FOR r IN
        SELECT t.oid::regtype AS name, t.typtype FROM pg_type t
        JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_roles o ON o.oid = t.typowner
        WHERE o.rolsuper AND n.nspname <> ALL (kept) AND n.nspname NOT LIKE 'pg\_%'
          AND t.typtype IN ('e', 'd', 'r')
          AND NOT EXISTS (SELECT FROM pg_depend d WHERE d.classid = 'pg_type'::regclass AND d.objid = t.oid AND d.deptype = 'e')
    LOOP
        EXECUTE format(CASE r.typtype WHEN 'd' THEN 'ALTER DOMAIN %s OWNER TO %I' ELSE 'ALTER TYPE %s OWNER TO %I' END, r.name, app);
    END LOOP;

    FOR r IN
        SELECT p.pubname FROM pg_publication p JOIN pg_roles o ON o.oid = p.pubowner WHERE o.rolsuper
    LOOP
        EXECUTE format('ALTER PUBLICATION %I OWNER TO %I', r.pubname, app);
    END LOOP;
END
$owners$;

-- What Debezium reads: every table already in the publication (later ones get their grant from their migration), and the heartbeat row
-- it updates (CdcMigrations.EnableHeartbeat).
DO $cdc$
DECLARE
    cdc text := current_setting('procureai.cdc_role');
    r record;
BEGIN
    IF cdc = '' THEN
        RETURN;
    END IF;
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', cdc);
    FOR r IN SELECT schemaname, tablename FROM pg_publication_tables WHERE pubname = 'dbz_publication' LOOP
        EXECUTE format('GRANT SELECT ON %I.%I TO %I', r.schemaname, r.tablename, cdc);
    END LOOP;
    IF to_regclass('public.debezium_heartbeat') IS NOT NULL THEN
        EXECUTE format('GRANT SELECT, UPDATE ON public.debezium_heartbeat TO %I', cdc);
    END IF;
END
$cdc$;

-- The old shared role's grants in this database (DROP OWNED also revokes its privileges); it owns nothing.
SELECT 'DROP OWNED BY debezium' WHERE EXISTS (SELECT FROM pg_roles WHERE rolname = 'debezium') \gexec

-- The operator's read-only role (roles.sql, plan 08b Task 4 fix round 1 M1), when it is on: USAGE on this database's schemas (the system
-- ones aside), SELECT on its tables, views and sequences, and by default on what the service's role creates later (migrations, Wolverine's
-- and Marten's runtime schemas and tables). Granted, not inherited from pg_read_all_data, so the system catalogs' secrets (pg_authid)
-- stay closed. GRANT and ALTER DEFAULT PRIVILEGES are idempotent.
\if :operator_enabled
SELECT format('GRANT USAGE ON SCHEMA %I TO operator_reader', nspname), format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO operator_reader', nspname),
       format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA %I TO operator_reader', nspname)
FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
ALTER DEFAULT PRIVILEGES FOR ROLE :"app_role" GRANT USAGE ON SCHEMAS TO operator_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE :"app_role" GRANT SELECT ON TABLES TO operator_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE :"app_role" GRANT SELECT ON SEQUENCES TO operator_reader;
\endif
