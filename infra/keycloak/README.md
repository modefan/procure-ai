# Keycloak realm `procure`

- `procure-realm.json` is imported at start (`--import-realm`). It holds no secrets: `${KC_DEMO_PASSWORD}`,
  `${KC_GATEWAY_SECRET}` and `${KC_INTERNAL_SECRET}` are replaced by Keycloak from environment variables (Aspire parameters `keycloak-demo-password`, `keycloak-gateway-secret`, `keycloak-internal-secret`).
- Tenants are Keycloak organizations; the `tenant` claim is the organization alias. Roles are explicit realm roles per demo user.
- Known gap: Keycloak cannot auto-join a self-registered user to an organization. A self-registered user therefore has
  no organization and no business role (no permissions: a safe default). Joining the `demo` organization and assigning
  `requester` is deferred to plan 02 (Access service through the Keycloak Admin API).
- The password-grant test client exists only in the tests, never in this file.
