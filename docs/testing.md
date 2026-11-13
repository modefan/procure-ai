# Tests and CI

What is tested where, and what CI runs. Back to the [README](../README.md).

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
automatically) or the generated Angular clients differ from what is committed. Every push to `main` that changes more than
the root Markdown files or `docs/` runs everything (`.github/workflows/ci.yml`): the build, the drift checks, the unit and architecture
tests, the Vitest specs, the chart checks with the AppHost's chart parity tests (the GitOps repo checked out beside it), the image
builds and image tests, the Testcontainers integration tests, the AppHost tests, and the system and Playwright end-to-end tests (after
the other suites passed). A green push run is followed by publishing (images and deploy). The same full run happens weekly when `main`
has moved by more than docs, and on demand (`workflow_dispatch`, optionally narrowed to some projects, or only the fast suites with
`profile: push`). Until the repo went public (plan 08c), the private repo's free 2 000 Actions minutes a month ran only the fast suites
on push, the long suites weekly, and the system and end-to-end tests not at all (the private 8 GB runner was killed while the whole
AppHost started; public repos get a 16 GB runner). CI generates random, per-run secrets; nothing sensitive is stored in the repo.
