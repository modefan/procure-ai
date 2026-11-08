# Keycloak realm `procure`

- `procure-realm.json` is imported at start (`--import-realm`). It holds no secrets: `${KC_DEMO_PASSWORD}`,
  `${KC_GATEWAY_SECRET}`, `${KC_INTERNAL_SECRET}`, `${KC_ACCESS_ADMIN_SECRET}` and `${KC_DEMO_LOGIN_SECRET}` are replaced by Keycloak from environment variables (Aspire parameters `keycloak-demo-password`, `keycloak-gateway-secret`, `keycloak-internal-secret`, `keycloak-access-admin-secret`, `keycloak-demo-login-secret`).
- Deployed clusters: Keycloak imports the file on its first start only; the platform chart's `keycloak-config` Job (keycloak-config-cli,
  plan 08 ruling 7) applies it again on every Argo CD sync, so a change here reaches an existing cluster (`docs/kubernetes.md`, "The realm
  comes from Git"). keycloak-config-cli fails on a client scope a client lists but the realm does not define (Keycloak skips it), so list
  only defined ones (`RealmFileTests`). The AppHost has no such Job: `start-dev` imports the file into a fresh container at every start.
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
- **Self-registration is off** (`registrationAllowed: false`, plan 08 Task 12c): nobody can create an account on the public demo's
  Keycloak, and the app shows no "Register" link (runtime config `registration.enabled`, off by default; `/register` goes to the login).
  The flow below stays in the code and its tests (the test Keycloak turns registration on in its own copy of this file); to offer it
  again, set both `registrationAllowed` here and `registration.enabled` in the web app's config.json.
- Self-registration (when on): Keycloak cannot auto-join a self-registered user to an organization, so the gateway does it. On the
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
- Tenant switch (plan 06 ruling 18): `POST /bff/switch-tenant` with `{ tenant, returnUrl }` and the `X-XSRF-TOKEN` header checks the
  membership with Access and answers `200 { navigateTo }` (never a redirect; a plain GET changes nothing). A demo session is switched
  inside the POST through the demo client. A code-flow session gets a single-use, 60-second URL (`/bff/switch-tenant/challenge?ticket=`,
  bound to that session) that asks Keycloak for `organization:<alias>` with `prompt=none` (one visible retry if Keycloak needs the
  user). Either way the gateway checks the token is for the same user and that tenant, and replaces the session with a new key.
- The password-grant test client `test-direct` exists only in the tests, never in this file.
- `argocd` is Argo CD's sign-in client (plan 08 Task 12c): public, code flow + PKCE (Argo CD's documented Keycloak setup; no secret),
  redirect URIs `${KC_ARGOCD_BASE_URL}/auth/callback` and `/pkce/verify` (the platform chart's `keycloak.argocdUrl`: the hosted
  port-forward `http://127.0.0.1:8080` by default, `https://argocd.procure.localtest.me` locally). Its own mappers put `sub`,
  `preferred_username`, `name`, `email` and `groups` (plain group names) into the ID token. Only members of the group `argocd-admins`
  get Argo CD's admin role (GitOps repo `argocd/values.yaml`); every other user who signs in gets no rights. No demo user is in the
  group; Dale adds his own user (docs/hosting.md, "Your own Keycloak user for Argo CD").
