#!/bin/bash
# First-start initialisation: the Debezium replication role, the requests database and an
# (initially empty) publication for Debezium. DEBEZIUM_PASSWORD comes from the AppHost parameter
# `debezium-password`; it is never stored in the repo.
set -euo pipefail

: "${DEBEZIUM_PASSWORD:?DEBEZIUM_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 -v dbz_password="$DEBEZIUM_PASSWORD" --username "$POSTGRES_USER" --dbname postgres <<'SQL'
CREATE ROLE debezium WITH LOGIN REPLICATION PASSWORD :'dbz_password';
CREATE DATABASE requestsdb;
SQL

# The publication starts empty because the Requests tables do not exist yet. A service's
# migration adds its tables later (ALTER PUBLICATION dbz_publication ADD TABLE ...).
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname requestsdb <<'SQL'
CREATE PUBLICATION dbz_publication;
GRANT USAGE ON SCHEMA public TO debezium;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO debezium;
SQL
