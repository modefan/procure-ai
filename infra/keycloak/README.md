# Keycloak realm `procure`

- `procure-realm.json` is imported at start (`--import-realm`). It holds no secrets: `${KC_DEMO_PASSWORD}`,
  `${KC_GATEWAY_SECRET}`, `${KC_INTERNAL_SECRET}` and `${KC_ACCESS_ADMIN_SECRET}` are replaced by Keycloak from environment variables (Aspire parameters `keycloak-demo-password`, `keycloak-gateway-secret`, `keycloak-internal-secret`, `keycloak-access-admin-secret`).
- Tenants are Keycloak organizations; the `tenant` claim is the organization alias. Roles are explicit realm roles per demo user.
- Self-registration: Keycloak cannot auto-join a self-registered user to an organization, so the gateway does it. On the
  first `/bff/user` call of a user with no tenant it asks Access (`POST internal/registrations`, on behalf of that user:
  Access refuses any other user with 403 and reads email and name from Keycloak, not from the call), which adds the user to
  the `demo` organization, gives them the realm role `requester` through the Keycloak Admin API and the sandbox cost center
  `DEMO-100` (its own budget, shared by all sign-ups), then the gateway refreshes
  the session tokens so the `tenant` claim appears. Until then (or if that fails) the user has no organization and no
  permissions, which is a safe default. Access logs in to the Admin API as `procure-access-admin` (a service account with
  `realm-management` roles; only Access has its secret). Its tokens are refused by every service API (no `procure-api`
  audience, `azp` is not `procure-internal`); `AdminTokenRefusedTests` checks that with real tokens.
- The password-grant test client exists only in the tests, never in this file.
