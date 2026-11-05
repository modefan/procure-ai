# Postgres 17 with pgvector (from the base image) plus pgaudit.
# Logical WAL, pgaudit, logging and memory settings are passed as server arguments by the Aspire AppHost.
FROM pgvector/pgvector:pg17

RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-17-pgaudit \
    && rm -rf /var/lib/apt/lists/*

# Runs once, on first start with an empty data directory (only the deployed Keycloak's role and database).
COPY initdb/ /docker-entrypoint-initdb.d/

# The per-service least-privilege roles (plan 08 ruling 6), run as the superuser on every start before the migrations: by the platform
# chart's db-roles Job from this image, by the AppHost's postgres-roles container (bind-mounted) and by the test fixtures.
COPY roles.sql roles-database.sql roles-pgaudit.sql /procureai/

# Streams the server's stderr log file to the console (logging_collector takes stderr into files), then runs the normal entrypoint.
COPY procureai-entrypoint.sh /usr/local/bin/procureai-entrypoint.sh
RUN chmod 755 /usr/local/bin/procureai-entrypoint.sh
ENTRYPOINT ["procureai-entrypoint.sh"]
CMD ["postgres"]
