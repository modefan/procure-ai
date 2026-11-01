#!/bin/bash
# First-start initialisation for the deployed Keycloak (plan 07 ruling 11): role `keycloak` and its database `keycloakdb` on this server.
# Only when KEYCLOAK_DB_PASSWORD is set: the chart sets it from Secret procure-keycloak (key db-password, made from the AppHost user
# secret keycloak-db-password). The AppHost runs Keycloak with start-dev on its own file database and does not set it, nor do the tests.
# Like every initdb script this runs once, on an empty data directory; a later password change needs ALTER ROLE by hand.
set -euo pipefail

# No `exit`: the image's entrypoint sources a script that is not executable, and an exit would end the entrypoint itself.
if [ -z "${KEYCLOAK_DB_PASSWORD:-}" ]; then
    echo "02-keycloak.sh: KEYCLOAK_DB_PASSWORD not set, no keycloak role or database."
else
    psql -v ON_ERROR_STOP=1 -v kc_password="$KEYCLOAK_DB_PASSWORD" --username "$POSTGRES_USER" --dbname postgres <<'SQL'
CREATE ROLE keycloak WITH LOGIN PASSWORD :'kc_password';
CREATE DATABASE keycloakdb OWNER keycloak;
REVOKE ALL ON DATABASE keycloakdb FROM PUBLIC;
SQL
fi
