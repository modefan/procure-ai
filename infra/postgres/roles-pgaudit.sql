-- The pgaudit summary's log tables in auditdb (plan 06 ruling 14); included by roles.sql right after auditdb's roles-database.sql, so
-- connected to auditdb as the superuser. Creating a file_fdw table over a server file needs the superuser, which Audit's own role
-- (audit_app) is not, so this lives here instead of Audit's --migrate (PgAuditLogSetup until plan 08 Task 4). Idempotent.
--   audit_log_reader  login, CONNECT on auditdb, USAGE on the schema pgaudit and SELECT on the seven log tables, nothing else. Not a
--                     member of pg_read_server_files and owner of no table, so it cannot point a table or COPY at another server file
--                     (both revoked here from volumes made by an earlier version). Password: DB_AUDIT_LOG_READER_PASSWORD (AppHost
--                     parameter audit-log-reader-password, the password of Audit's ConnectionStrings:auditlog).
--   file_fdw, the server pgaudit_log and, in the schema pgaudit, one foreign table per weekday, log_mon ... log_sun, each over that
--   weekday's csvlog (log_filename=postgresql-%a.log, so log/postgresql-Mon.csv ...), all owned by the superuser. The schema and the file
--   names are the defaults of Audit's PgAudit:Schema and PgAudit:LogFileTemplate (appsettings.json); change them together.

\unset reader_password
\getenv reader_password DB_AUDIT_LOG_READER_PASSWORD
\if :{?reader_password}
SELECT :'reader_password' = '' AS reader_password_empty \gset
\else
\set reader_password_empty true
\endif
\if :reader_password_empty
DO $missing$ BEGIN RAISE EXCEPTION 'roles.sql: no password for role audit_log_reader (DB_AUDIT_LOG_READER_PASSWORD is not set or empty)'; END $missing$;
\endif

SELECT 'CREATE ROLE audit_log_reader' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'audit_log_reader') \gexec
ALTER ROLE audit_log_reader WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'reader_password';
SELECT 'REVOKE pg_read_server_files FROM audit_log_reader' WHERE pg_has_role('audit_log_reader', 'pg_read_server_files', 'MEMBER') \gexec
GRANT CONNECT ON DATABASE auditdb TO audit_log_reader;

CREATE EXTENSION IF NOT EXISTS file_fdw;
CREATE SERVER IF NOT EXISTS pgaudit_log FOREIGN DATA WRAPPER file_fdw;
REVOKE ALL ON FOREIGN SERVER pgaudit_log FROM PUBLIC, audit_log_reader;
CREATE SCHEMA IF NOT EXISTS pgaudit;
ALTER SCHEMA pgaudit OWNER TO CURRENT_USER;
REVOKE ALL ON SCHEMA pgaudit FROM PUBLIC, audit_log_reader;
GRANT USAGE ON SCHEMA pgaudit TO audit_log_reader;
DROP FOREIGN TABLE IF EXISTS pgaudit.log_today;

DO $logs$
DECLARE
    day text;
    tbl text;
    file text;
    -- The csvlog columns of Postgres 14 to 17 (backend_type, leader_pid and query_id came with 14).
    columns text := 'log_time timestamp(3) with time zone, user_name text, database_name text, process_id integer, connection_from text, '
        'session_id text, session_line_num bigint, command_tag text, session_start_time timestamp with time zone, '
        'virtual_transaction_id text, transaction_id bigint, error_severity text, sql_state_code text, message text, detail text, '
        'hint text, internal_query text, internal_query_pos integer, context text, query text, query_pos integer, location text, '
        'application_name text, backend_type text, leader_pid integer, query_id bigint';
BEGIN
    FOREACH day IN ARRAY ARRAY['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] LOOP
        tbl := 'pgaudit.' || quote_ident('log_' || lower(day));
        file := 'log/postgresql-' || day || '.csv';
        EXECUTE format('CREATE FOREIGN TABLE IF NOT EXISTS %s (%s) SERVER pgaudit_log OPTIONS (filename %L, format ''csv'')', tbl, columns, file);
        EXECUTE format('ALTER FOREIGN TABLE %s OWNER TO CURRENT_USER', tbl);
        EXECUTE format('ALTER FOREIGN TABLE %s OPTIONS (SET filename %L)', tbl, file);
        EXECUTE format('REVOKE ALL ON %s FROM PUBLIC, audit_log_reader', tbl);
        EXECUTE format('GRANT SELECT ON %s TO audit_log_reader', tbl);
    END LOOP;
END
$logs$;
