# Run it locally

Everything starts under .NET Aspire with one command. Back to the [README](../README.md).

## Run it locally

Prerequisites: Docker, .NET 10 SDK, Node 26 LTS (see `.nvmrc`).

1. Set the AppHost user secrets once (values are yours to choose; use long random strings):

   ```powershell
   $apphost = "src/AppHost/ProcureAi.AppHost"
   foreach ($name in "keycloak-admin-password", "keycloak-demo-password", "keycloak-gateway-secret", "keycloak-internal-secret", "keycloak-access-admin-secret", "simulator-api-key", "supplier-webhook-secret") {
     dotnet user-secrets set "Parameters:$name" ([Convert]::ToHexString((1..24 | % { Get-Random -Max 256 }) -as [byte[]])) --project $apphost
   }
   # admin-api-key-sha256 is the SHA-256 (hex) of the one admin API key (X-Api-Key, for scripts and tests):
   $key = [Convert]::ToHexString((1..32 | % { Get-Random -Max 256 }) -as [byte[]])
   $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
   dotnet user-secrets set "Parameters:admin-api-key-sha256" $sha --project $apphost
   # the key itself, for the demo data seeder (it calls the gateway with it):
   dotnet user-secrets set "Parameters:admin-api-key" $key --project $apphost
   ```

   The end-to-end and system tests also read the key itself from the `DEMO_ADMIN_API_KEY` environment variable.
2. Start everything: `dotnet run --project src/AppHost/ProcureAi.AppHost`
3. Open the Aspire dashboard link printed in the console, then the `gateway` endpoint to use the app.

After pulling changes to a demo seed, reset the local data volumes once: the seed only adds rows that are missing, so an old
volume keeps old rows (for example the sandbox cost center DEMO-100 without its manager).

Memory and CPU caps for every container come from the `Resources` section of the AppHost `appsettings.json`.

**Resource needs (measured 2026-11-01 with plan 06 complete: the whole system idle after the demo data seed, `docker stats --no-stream`
plus the working sets of the .NET processes):**

| Part | Memory |
|---|---|
| Containers: eight Debezium Servers (0.28 to 0.31 GiB each: seven Postgres databases and MongoDB) | 2.4 GiB |
| Containers: Keycloak 0.67, MongoDB 0.40, Postgres with pgvector and pgaudit 0.34, RabbitMQ 0.15, Mailpit and Redis 0.04 | 1.6 GiB |
| **All containers** | **3.9 GiB** |
| Ten services (Requests 0.48, Invoicing 0.48, Purchasing 0.48, Notifications 0.44, Approvals 0.38, Suppliers 0.38, Assistant 0.35, Access 0.33, Budgets 0.32, Audit 0.31) | 3.9 GiB |
| Gateway 0.17, simulators 0.11, AppHost 0.20 | 0.5 GiB |
| **All .NET processes of the system** | **4.3 GiB** |
| Local development only: Aspire's DCP and its proxies (1.7), the `dotnet run` launchers and the Angular dev server | about 3 GiB |

So the system itself needs about 8.2 GiB (the eighth Debezium Server, for MongoDB, adds about 0.3 GiB); a deployment without the local
tooling fits a 16 GB VM. Running it locally under Aspire takes about 11 GiB. Postgres runs with `max_connections` 200 locally
(`Resources:Postgres:MaxConnections`): ten services with a few connection pools each went past the default of 100 during the seed. The
figures above were measured just before that change, with 100; the extra idle connections add little (Postgres used 0.34 of its 0.75 GiB
cap with about 115 connections open).
