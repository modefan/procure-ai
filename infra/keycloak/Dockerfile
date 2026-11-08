# Keycloak for deployments (plan 07): the build-time options are baked in with `kc.sh build`, so the container starts with
# `start --optimized` (fast start, no build at every restart). Build context: this folder (infra/keycloak).
# The local AppHost does not use this image: it runs the stock image with `start-dev` and bind-mounts this folder.
# Keep the version in step with src/AppHost AppHost.cs and tests/ProcureAi.Testing/KeycloakFixture.cs.
# Runtime settings (hostname, database URL and credentials, http/proxy mode, the KC_* realm placeholders, bootstrap admin) are
# environment variables set by the chart; none is baked in. The realm holds no secrets (${KC_...} placeholders).
FROM quay.io/keycloak/keycloak:26.8.1 AS builder
ENV KC_DB=postgres \
    KC_HEALTH_ENABLED=true
RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.8.1
COPY --from=builder /opt/keycloak/ /opt/keycloak/
COPY procure-realm.json /opt/keycloak/data/import/procure-realm.json
# Realm ops: Argo CD's sign-in (OTP required, brute-force lockout on), apart from the public demo realm (plan 08 fix wave ruling R6c).
COPY ops-realm.json /opt/keycloak/data/import/ops-realm.json
ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--optimized", "--import-realm"]
