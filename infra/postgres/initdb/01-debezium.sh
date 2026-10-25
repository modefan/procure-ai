#!/bin/bash
# First-start initialisation: only the Debezium replication role. Databases are created by Aspire (and by the tests);
# each service's migration creates its own publication and grants (CdcMigrations.EnableCdc in BuildingBlocks.Persistence).
# DEBEZIUM_PASSWORD comes from the AppHost parameter `debezium-password`; it is never stored in the repo.
set -euo pipefail

: "${DEBEZIUM_PASSWORD:?DEBEZIUM_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 -v dbz_password="$DEBEZIUM_PASSWORD" --username "$POSTGRES_USER" --dbname postgres <<'SQL'
CREATE ROLE debezium WITH LOGIN REPLICATION PASSWORD :'dbz_password';
SQL
