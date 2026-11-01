# Debezium Server for deployments (plan 07): the stock image plus the repository's two properties files.
# Build context: this folder (infra/debezium). The local AppHost does not use this image: it bind-mounts application.properties.
#   /debezium/config/application.properties        one Postgres database -> RabbitMQ exchange "cdc" (one instance per captured database)
#   /debezium/config/application-mongo.properties  the MongoDB change streams -> the same exchange (the one debezium-mongo instance)
# The chart picks the MongoDB file (Quarkus `-Dquarkus.config.locations`, or by mounting a file over application.properties).
# Per-instance values (database, slot, include list, credentials) are environment variables, as in the AppHost.
# Keep the version in step with src/AppHost ServiceResources.cs and the Debezium fixtures in tests/ProcureAi.Testing.
FROM quay.io/debezium/server:3.7.0.Final
COPY application.properties application-mongo.properties /debezium/config/
