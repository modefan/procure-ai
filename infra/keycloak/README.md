# Keycloak realm `procure`

- `procure-realm.json` is imported at start (`--import-realm`). It holds no secrets: `${KC_DEMO_PASSWORD}`,
  `${KC_GATEWAY_SECRET}`, `${KC_INTERNAL_SECRET}`, `${KC_ACCESS_ADMIN_SECRET}` and `${KC_DEMO_LOGIN_SECRET}` are replaced by Keycloak from environment variables (Aspire parameters `keycloak-demo-password`, `keycloak-gateway-secret`, `keycloak-internal-secret`, `keycloak-access-admin-secret`, `keycloak-demo-login-secret`).
- Tenants are Keycloak organizations; the `tenant` claim is the organization alias. Roles are explicit realm roles per demo user.
- **Exactly one tenant per token.** The `tenant` claim comes from the single-valued organization mapper in the `organization` client
  scope, and that is the only scope with an organization mapper. Keycloak 26 treats *every* client scope that holds an
  `oidc-organization-membership-mapper` as an organization scope (`OrganizationScope.valueOfScope`) and takes the first one it finds in
  the requested scopes. When the mapper lived in `procure-api` too, a request for `organization:acme` held two of them (`procure-api`
  meaning "any organization" and `organization:acme`), the first one won, and its order varied: a user in two organizations got the
  tenant only sometimes (seen in plan 06 Task 13). `RealmTests.Only_the_organization_scope_maps_organizations` keeps it that way.
  - `organization` is a default scope of the gateway, Swagger and demo login clients, so a user in one organization gets it without
    asking.
  - A user in several organizations (Thandi: Acme and Globex) gets a tenant only by naming one: the gateway's code flow asks for scope
    `organization` (`Oidc:Scopes`), so Keycloak shows its organization picker during the login; the demo login and the tenant switch
    ask for `organization:<alias>`. Without a choice there is no tenant (never an arbitrary one). The bare `organization` and
    `organization:<alias>` cannot be sent together (Keycloak answers `invalid_scope`); `OrganizationScope.For` replaces the one with the
    other. A refresh keeps the organization of the login.
- Self-registration: Keycloak cannot auto-join a self-registered user to an organization, so the gateway does it. On the
  first `/bff/user` call of a user with no tenant it asks Access (`POST internal/registrations`, on behalf of that user:
  Access refuses any other user with 403, refuses a user who already belongs to another organization with 409, and reads
  email and name from Keycloak, not from the call), which adds the user to
  the `demo` organization, gives them the realm role `requester` through the Keycloak Admin API and the sandbox cost center
  `DEMO-100` (its own budget, shared by all sign-ups), then the gateway refreshes
  the session tokens so the `tenant` claim appears. Until then (or if that fails) the user has no organization and no
  permissions, which is a safe default. Access logs in to the Admin API as `procure-access-admin` (a service account with
  `realm-management` roles; only Access has its secret). Its tokens are refused by every service API (no `procure-api`
  audience, `azp` is not `procure-internal`); `AdminTokenRefusedTests` checks that with real tokens.
- `procure-demo-login` is the gateway's one-click demo login client (plan 06 ruling 17): confidential, direct access grants (password
  grant) only, no browser flow. The gateway uses it only for the users listed in its `DemoLogin:Users`, and only when
  `DemoLogin:Enabled`. It asks for `scope=openid organization:<tenant>`, so the token carries exactly that tenant.
- Tenant switch (plan 06 ruling 18): `GET /bff/switch-tenant?tenant=<alias>` checks the membership with Access, then asks Keycloak for
  `organization:<alias>` with `prompt=none` (code-flow session; one visible retry if Keycloak needs the user) or through the demo client
  (demo session), checks the token is for the same user and that tenant, and replaces the session with a new key.
- The password-grant test client `test-direct` exists only in the tests, never in this file.
