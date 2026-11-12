-- Per-service least-privilege database users (plan 08 ruling 6). Run by psql as the server's superuser on EVERY start, before any
-- migration: under the AppHost by the one-shot container `postgres-roles` (ServiceResources.AddPostgresRoles), in Kubernetes by the
-- db-roles Sync hook Job of the platform chart (templates/db-roles-job.yaml), in the integration tests by PostgresFixture. It is
-- idempotent: a second run changes nothing (and fails nothing), a changed password is applied by the next run.
--
--   psql -v ON_ERROR_STOP=1 -U postgres -d postgres -f roles.sql
--
-- Per service database <service>db:
--   <service>_app  LOGIN, no superuser / createdb / createrole / replication / bypassrls. Owns <service>db, its schemas and every object
--                  in them, so its migration creates, alters and truncates (the demo reset's TRUNCATE audit_entries) its own objects and
--                  cannot even connect to another service's database (CONNECT is revoked from PUBLIC).
--   <service>_cdc  LOGIN REPLICATION, for the one Debezium Server of that database: CONNECT, USAGE on public, SELECT on the tables of the
--                  publication dbz_publication and SELECT, UPDATE on debezium_heartbeat. A table a later migration adds to the
--                  publication gets its grant from the migration itself (CdcMigrations.EnableCdc reads the role name from the database
--                  setting procureai.cdc_role set here), this script re-applies them all on every run.
-- Plus audit_log_reader (Audit's pgaudit summary: SELECT on the seven file_fdw log tables in auditdb, nothing else) and the extensions
-- the migrations cannot create as their own role (vector in assistantdb, file_fdw in auditdb).
-- The old shared replication role `debezium` (plan 01 to plan 08 Task 1) loses its grants and is dropped.
--
-- Passwords come from environment variables, never from this file or the command line: DB_<SERVICE>_PASSWORD, DB_<SERVICE>_CDC_PASSWORD
-- and DB_AUDIT_LOG_READER_PASSWORD (AppHost parameters db-<service>-password, db-<service>-cdc-password, audit-log-reader-password;
-- Kubernetes Secret procure-db-roles). A missing one fails the script before anything changes for that service.
-- pgaudit logs every ALTER ROLE ... PASSWORD with the password replaced by <REDACTED>.
--
-- Existing volumes (made when every service connected as the superuser): the per-database part moves the ownership of the database,
-- its schemas, tables, sequences, views, functions, types and publications from the superuser to <service>_app (REASSIGN OWNED BY
-- postgres is impossible: postgres also owns the system catalogs), leaving extension members and the pgaudit schema with the superuser.

\set ON_ERROR_STOP on
\set QUIET on
SET client_min_messages = warning;
-- A statement that fails is logged with its text (log_min_error_statement=error by default), and an ALTER ROLE ... PASSWORD would put the
-- password in plain text into the csvlog that audit_log_reader and the log collector read. Never log statement texts of this session
-- (the error itself is still logged). Every \connect starts a new session, so roles-database.sql sets it again after each one.
SET log_min_error_statement = panic;

-- The operator's read-only role (plan 08b Task 4): what pgAdmin on the public operator host connects as by default, so looking at the data
-- cannot change it. Only where DB_OPERATOR_READER_PASSWORD is set (the clusters: Secret procure-db-roles, key operator-reader-postgres-password);
-- the AppHost and the tests do not set it, and a role left from an earlier run then loses its login (end of this file). Made here, before
-- the service databases, so roles-database.sql can grant it read access in each one (operator_enabled); its CONNECT grants are at the end.
--   operator_reader  LOGIN, default_transaction_read_only. In each service database (roles-database.sql): USAGE on its schemas, SELECT on
--                    its tables, views and sequences, and the same by default for what the service's own role creates later (migrations,
--                    Wolverine's and Marten's runtime tables). Not pg_read_all_data (fix round 1 M1): that role also reads the system
--                    catalogs, among them pg_authid with every role's password verifier. No write of any kind, no DDL, no replication;
--                    it owns nothing.
\unset operator_password
\getenv operator_password DB_OPERATOR_READER_PASSWORD
\if :{?operator_password}
SELECT :'operator_password' <> '' AS operator_enabled \gset
\else
\set operator_enabled false
\endif
\if :operator_enabled
SELECT 'CREATE ROLE operator_reader' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'operator_reader') \gexec
ALTER ROLE operator_reader WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT PASSWORD :'operator_password';
-- Read-only for good: no default transaction that could write, even if a grant ever slipped through.
ALTER ROLE operator_reader SET default_transaction_read_only = on;
-- An earlier revision made it a member of pg_read_all_data (pg_authid included); taken back.
SELECT 'REVOKE pg_read_all_data FROM operator_reader' WHERE EXISTS (
    SELECT FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles u ON u.oid = m.member
    WHERE g.rolname = 'pg_read_all_data' AND u.rolname = 'operator_reader') \gexec
\endif

\set service access
\set database accessdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_ACCESS_PASSWORD
\getenv cdc_password DB_ACCESS_CDC_PASSWORD
\ir roles-database.sql

\set service approvals
\set database approvalsdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_APPROVALS_PASSWORD
\getenv cdc_password DB_APPROVALS_CDC_PASSWORD
\ir roles-database.sql

\set service assistant
\set database assistantdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_ASSISTANT_PASSWORD
\getenv cdc_password DB_ASSISTANT_CDC_PASSWORD
\ir roles-database.sql
-- pgvector: the Assistant's model asks for it (HasPostgresExtension), and CREATE EXTENSION needs the superuser. Its migration's
-- CREATE EXTENSION IF NOT EXISTS vector then only notices that it exists.
CREATE EXTENSION IF NOT EXISTS vector;

\set service audit
\set database auditdb
\set capture false
\unset app_password
\unset cdc_password
\getenv app_password DB_AUDIT_PASSWORD
\ir roles-database.sql
\ir roles-pgaudit.sql

\set service budgets
\set database budgetsdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_BUDGETS_PASSWORD
\getenv cdc_password DB_BUDGETS_CDC_PASSWORD
\ir roles-database.sql

\set service invoicing
\set database invoicingdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_INVOICING_PASSWORD
\getenv cdc_password DB_INVOICING_CDC_PASSWORD
\ir roles-database.sql

\set service purchasing
\set database purchasingdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_PURCHASING_PASSWORD
\getenv cdc_password DB_PURCHASING_CDC_PASSWORD
\ir roles-database.sql

\set service requests
\set database requestsdb
\set capture true
\unset app_password
\unset cdc_password
\getenv app_password DB_REQUESTS_PASSWORD
\getenv cdc_password DB_REQUESTS_CDC_PASSWORD
\ir roles-database.sql

-- The operator's read-only role (made at the top of this file): CONNECT on every database that allows connections except keycloakdb (it
-- reads only the service databases, where roles-database.sql granted it SELECT).
-- keycloakdb is left out (plan 08b Task 4 ruling R1): it holds password hashes, one-time-code seeds, client secrets and the realms' signing
-- keys in many tables, and Keycloak's own schema is not ours to change (no row-level security on its tables). Its CONNECT is revoked
-- should an earlier run have granted it (PUBLIC has no CONNECT on any database here). Keycloak's data is looked at in its admin console,
-- or as the superuser in an emergency.
\connect postgres
SET client_min_messages = warning;
SET log_min_error_statement = panic;
\if :operator_enabled
SELECT format('GRANT CONNECT ON DATABASE %I TO operator_reader', datname) FROM pg_database
WHERE datallowconn AND NOT datistemplate AND datname <> 'keycloakdb' \gexec
SELECT 'REVOKE CONNECT ON DATABASE keycloakdb FROM operator_reader' WHERE EXISTS (SELECT FROM pg_database WHERE datname = 'keycloakdb') \gexec
\else
SELECT 'ALTER ROLE operator_reader NOLOGIN' WHERE EXISTS (SELECT FROM pg_roles WHERE rolname = 'operator_reader') \gexec
\endif

-- The old shared replication role: its grants went with DROP OWNED in every database above. Should it still hold a privilege somewhere
-- else (a database not listed here), it stays, without login, and the script says so instead of failing the deployment.
\connect postgres
SET log_min_error_statement = panic;
DO $drop$
BEGIN
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'debezium') THEN
        ALTER ROLE debezium NOLOGIN NOREPLICATION;
        BEGIN
            DROP ROLE debezium;
        EXCEPTION WHEN dependent_objects_still_exist THEN
            RAISE WARNING 'roles.sql: role debezium still holds privileges elsewhere (%); it is kept without login', SQLERRM;
        END;
    END IF;
END
$drop$;
\echo 'roles.sql: done'
