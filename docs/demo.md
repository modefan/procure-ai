# Demo users and demo data

Who you can sign in as, how the one-click demo login works, the seeded data, the nightly reset and the invoice scan sample. Back to the [README](../README.md).

## Demo users

**One click:** the welcome page shows **Try a demo role** buttons. Each signs you in as one demo user, no password typed.
Or sign in through Keycloak as usual; the demo password is the `keycloak-demo-password` parameter set in [Run it locally](local-run.md) (on Kubernetes the cluster Secrets are made from the same values).

| Button | User | Tenant | Role |
|---|---|---|---|
| Requester (Tom) | tom@acme.test | Acme | requester |
| Manager (Maria) | maria@acme.test | Acme | manager |
| Department head | dept@acme.test | Acme | department-head |
| Finance (Thandi) | thandi@acme.test | Acme and Globex | finance in both (**Switch company** in her user menu) |
| Admin | admin@acme.test | Acme | admin |
| Globex manager (Ben) | ben@globex.test | Globex | manager |
| (Keycloak login only) | approver@demo.test | Demo | manager (approves requests on the sandbox cost center DEMO-100 of self-registered users, when self-registration is on) |

**Self-registration is off on the public demo** (plan 08 Task 12c): the realm does not allow it (`registrationAllowed: false` in
`infra/keycloak/procure-realm.json`), the web app shows no "Register" (runtime config `registration.enabled`, default false) and the
gateway joins nobody to the Demo company (`Registration:Enabled`, default false). Reviewers use the demo users above. When all three are
switched on together, a new user registers through Keycloak's form, joins the Demo company as a requester on DEMO-100 at the first page
load, and approver@demo.test approves their requests.

**Thandi through the normal Keycloak login:** the sign-in asks for the email first. Thandi belongs to two organizations, so
Keycloak then shows its own **organization picker** ("Select an organization to proceed": Globex or Acme Corp) and only after that
the password. The organization picked is the company the session starts in; **Switch company** in her user menu changes it later.
The **Finance (Thandi)** button skips the picker (it starts in Acme). Every other demo user is in one organization and goes from the
email straight to the password.

How the buttons work: the gateway (`POST /bff/demo-login` with `{ "user": "tom" }`, XSRF header required) signs the user in with
the OAuth password grant on a **separate** Keycloak client, `procure-demo-login` (confidential, direct access grants only, no
browser flow), and then creates exactly the session the normal code-flow login creates: tokens in the Redis ticket store, the
same `__Host-` cookie, claims, refresh and logout. The browser only ever sends a key from the configured list (`DemoLogin:Users`
in the gateway's appsettings); the username and password come from the gateway's configuration. The password grant is deprecated
in OAuth 2.1, so it is used for these listed demo users only and is **off by default** (`DemoLogin:Enabled`): when off, both
`/bff/demo-login` endpoints answer 404 and the welcome page shows no buttons. The AppHost switches it on and generates the demo
client's secret on the first run (AppHost user secret `Parameters:keycloak-demo-login-secret`).

### Demo data

On every start the AppHost runs `demo-data` (`tools/ProcureAi.DemoData seed`) once the gateway is healthy. It drives purchase
requests through every state in both companies through the **public APIs, via the gateway**: the admin API key acts on behalf of the
demo users (`X-On-Behalf-Of-User`), so Tom raises and submits, Maria and Dept approve, Tom receives and Thandi pays, and every record
has its saga, budget reservation, events, audit trail and data changes, exactly as if the people had clicked. The scenarios are data
(`tools/ProcureAi.DemoData/Scenarios/acme.json`, `globex.json`, one request per state: draft, waiting for the manager, the department
head or finance, rejected by the manager, rejected over budget, cancelled, ordered, partly received, received, invoiced and matched,
price mismatch, partly paid, completed, closed short; Globex: waiting, ordered, completed, rejected). Maria's own request on IT-200
("Monitors for the IT managers' desks", PR-0016 on fresh data) shows separation of duties: the **Access check** answers that Maria may
not approve it because she raised it, so it waits for the department head.

The seed is safe to run again: it finds each request by title and requester and resumes it from where it is, and every write carries the
Idempotency-Key `demo:<tenant>:<key>:<step>`, so nothing is created twice. It ends with exit code 0, or 1 naming the request and the
step that failed. It takes about two minutes on a fresh start (the simulated supplier confirms after 3 s and invoices after 20 s).

### Nightly reset

Reviewers can change anything, so the demo starts fresh every night at 02:00 (Africa/Johannesburg): `ProcureAi.DemoData reset` first checks
that every service and the simulators have the reset switched on (else it stops before touching anything), then resets the
simulators, then every service in parallel through its internal `POST .../internal/demo/reset` (admin key or service account only, and
404 unless `DemoReset:Enabled`), then Audit last, and seeds again. Keycloak is not touched (when self-registration is on, the users
who registered themselves into the Demo company stay). Each service deletes with its normal write paths, so the audit trail shows the reset as the app's own changes,
never as "changed outside the app". `ProcureAi.DemoData schedule` runs it every night; locally, the dashboard command **Reset demo data**
on the `demo-data` resource runs it once. There is no reset button in the app.

### Switching the demo off

To save hosting costs the whole demo can be switched off without stopping anything: Access keeps one `system_settings` row (no row = on),
and while it is off the gateway answers every browser call to `/api/**`, `/bff/**` and the hubs with 503 ProblemDetails of type
`https://procure-ai/problems/system-disabled` (with `Retry-After`), before sign-in and before anything reaches a service. The web app asks
the switch before it starts and shows only its maintenance page ("This demo is switched off", the stored message or a default text, and a
"Request access" link when `maintenance.contactUrl` is set in config.json). The switch itself, logging out (`/bff/logout`, `/bff/logout-all`), health,
Swagger UI, the web app's files and calls with the admin API key keep working. The gateway reads the switch every `SystemAvailability:CacheDuration` (5 s); if Access cannot be
reached it keeps the last answer, and if it never had one it lets calls through. The nightly reset never changes the switch; every change is
in the data change audit (`system_settings`, by `claude-admin`), and Debezium captures the table too, so a switch made outside the app
(manual SQL) shows up as an unmatched change.

Only the admin API key may switch it (`PUT`, any user token is 403); anyone may read it (`GET /api/v1/system/availability`):

```powershell
$headers = @{ 'X-Api-Key' = $env:DEMO_ADMIN_API_KEY }
# Off, with an optional message for the maintenance page:
Invoke-RestMethod -Method Put -Uri https://<host>/api/v1/system/availability -Headers $headers -ContentType 'application/json' `
    -Body '{ "enabled": false, "message": "The demo is paused to save costs. Ask for access to have it switched on." }'
# On again:
Invoke-RestMethod -Method Put -Uri https://<host>/api/v1/system/availability -Headers $headers -ContentType 'application/json' -Body '{ "enabled": true }'
```

```bash
curl -X PUT https://<host>/api/v1/system/availability -H "X-Api-Key: $DEMO_ADMIN_API_KEY" -H 'Content-Type: application/json' \
  -d '{"enabled": false, "message": "Paused until Monday."}'
curl -X PUT https://<host>/api/v1/system/availability -H "X-Api-Key: $DEMO_ADMIN_API_KEY" -H 'Content-Type: application/json' -d '{"enabled": true}'
```

Live connections (SignalR) that were open when the demo was switched off are not cut: the gateway only refuses new requests, so an open
connection keeps carrying presence heartbeats until the browser's next HTTP call or reconnect gets the 503, which takes it to the
maintenance page and stops the connection. The gateway reads the switch with its service-account token, so Access counts the read in the internal service
accounts' rate-limit partition (the shared `procure-internal` client, also used by the other services' calls to Access), which no visitor can
reach: a flood of anonymous reads through the gateway cannot starve it. When it cannot get a token, it reads anonymously. The web app's own
anonymous reads of the switch all reach Access from the gateway's address, so they share one anonymous rate-limit partition there: a
visitor flooding them (within the gateway's per-address limit) can make other visitors' reads fail, and the web app then starts as if the
demo were on (fail open). Accepted: the 1 s read cache absorbs most of such a flood and the gateway's own enforcement is unaffected. The web app waits at most
`maintenance.checkTimeoutMs` (config.json, 4 s) for the switch at start-up, then starts as if it were on.

This is separate from the nightly reset's short maintenance window (writes only, a Redis flag with a TTL).

### Try the invoice scan

Acme's first order, **PO-1001**, is seeded from Fabrikam Office: 2 × USB-C Dock and 2 × Laptop Stand, received in full. Fabrikam posts
PDF invoices instead of sending invoice webhooks (`Simulators:Supplier:ManualInvoiceSuppliers`), so PO-1001 waits for its invoice. The
sample invoice [`samples/invoice-PO-1001.pdf`](samples/invoice-PO-1001.pdf) (Fabrikam Office, R 6 992,00 incl. VAT) names it:

1. Sign in as tom@acme.test and open **Invoices**.
2. Drop the sample PDF on **Scan an invoice**, then **Review and save**: PO-1001 is chosen, the invoice number, date, quantities and
   total are filled in, and nothing is saved until you press **Save invoice**.

The end-to-end test `Tom_scans_invoice_reviews_and_saves` scans the same invoice (`tests/ProcureAi.EndToEndTests/InvoicePdf.cs`,
`FabrikamPo1001`); `SampleInvoiceTests` keeps the committed PDF the same (`UPDATE_SAMPLES=1` writes it again).
