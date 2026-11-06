# Tests and CI

What is tested where, and how CI spends its minutes while the repo is private. Back to the [README](../README.md).

## Tests and CI

`dotnet test --project tests/<project>` per project (xUnit v3 on Microsoft.Testing.Platform). Unit, architecture
(layer rules), Testcontainers integration tests (Postgres, Redis, Keycloak, RabbitMQ), an Aspire AppHost test, and
Playwright end-to-end tests (live approval without reload, presence, conflict dialog, forbidden pages, logout isolation, reconnect,
the assistant's streamed answer and Stop, policy upload to Ready, invoice scan to a saved invoice, each demo role's menu, the full
flow from request to payment through the UI only, a matrix change reaching a signed-in user within 10 s, the access check, the chaos
switch opening and closing the breaker, an SQL change behind the app's back on the data changes and status screens, the company switch,
and the dev panel's cache HIT and trace link), about 770 Vitest specs for the Angular app, plus a system test (`dotnet run --project tests/ProcureAi.SystemTests`) that starts the
whole AppHost and drives a request through approval, purchasing, invoicing and audit, and asks the assistant through the
gateway. The assistant's retrieval is measured on every run (hit@6 on 34 questions); its model-judged evaluation
(`tests/ProcureAi.Assistant.Evaluation`, groundedness and relevance) needs a real provider and is run by hand. GitHub Actions builds with warnings as errors and fails
the build if any of the services' OpenAPI documents (found by convention, so a new service is covered
automatically) or the generated Angular clients differ from what is committed. **While the repo is private**, every Actions
minute counts against the free 2 000 a month, so CI splits the suites (`.github/workflows/ci.yml`): every push that changes more than
the root Markdown files or `docs/` runs the build, the drift checks, the unit and architecture tests, the Vitest specs and the chart checks with the AppHost's chart
parity tests (the GitOps repo checked out beside it), about 15 billed minutes plus about 8 for publishing (images and deploy). The long suites (image
builds and image tests, Testcontainers integration tests and the AppHost tests) run weekly when `main` has moved by more than docs
and on demand (`workflow_dispatch`, optionally narrowed to some projects); the .NET suites alone take about 45 minutes. The system
tests and the Playwright end-to-end tests are **off in CI until fixed**: the private repo's 8 GB runner was killed while the whole
AppHost started, so they run locally before pushes for now. **When the repo goes public** (unlimited minutes, a 16 GB runner) the long
suites move back to every push and the system and end-to-end tests are switched back on. CI generates random, per-run secrets; nothing sensitive is stored in the repo.
