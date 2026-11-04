#!/bin/bash
# First-start initialisation for the deployed Keycloak (plan 07 ruling 11): role `keycloak` and its database `keycloakdb` on this server.
# Only when KEYCLOAK_DB_PASSWORD is set: the chart sets it from Secret procure-keycloak (key db-password, made from the AppHost user
# secret keycloak-db-password). The AppHost runs Keycloak with start-dev on its own file database and does not set it, nor do the tests.
# Like every initdb script this runs once, on an empty data directory; a later password change needs ALTER ROLE by hand.
# The image's entrypoint executes an executable *.sh and sources one that is not. Its mode in the image depends on the build host
# (committed 100755; a Windows build context makes every file executable, a Linux one keeps the git mode), so this script works both
# ways: no `exit` (sourced, it would end the entrypoint) and no `set` (sourced, it would change the entrypoint's options). A failed
# psql still stops the init: ON_ERROR_STOP makes psql exit non-zero, and the entrypoint runs under `set -e`.
if [ -z "${KEYCLOAK_DB_PASSWORD:-}" ]; then
    echo "02-keycloak.sh: KEYCLOAK_DB_PASSWORD not set, no keycloak role or database."
else
    psql -v ON_ERROR_STOP=1 -v kc_password="$KEYCLOAK_DB_PASSWORD" --username "$POSTGRES_USER" --dbname postgres <<'SQL'
CREATE ROLE keycloak WITH LOGIN PASSWORD :'kc_password';
CREATE DATABASE keycloakdb OWNER keycloak;
REVOKE ALL ON DATABASE keycloakdb FROM PUBLIC;
SQL
fi
