# ProcureAI

A multi-tenant procurement system in South African Rand (ZAR): employees raise purchase requests, a rules-driven approval chain runs with budget reservation, approved requests become purchase orders, and invoices are three-way matched and exported for payment. Built as a portfolio demo with .NET 10 microservices, an Angular 22 frontend, Wolverine over RabbitMQ, .NET Aspire, a Keycloak-backed BFF gateway, and an AI policy assistant with invoice scanning.

Run locally (needs Docker): `dotnet run --project src/AppHost/ProcureAi.AppHost`. This starts Postgres (pgvector + pgaudit), MongoDB (replica set), Redis, RabbitMQ, Keycloak, Mailpit and Debezium Server with memory caps from the `Resources` section of `src/AppHost/ProcureAi.AppHost/appsettings.json`. Secrets are AppHost user secrets: `Parameters:keycloak-admin-password`, `Parameters:debezium-password`, `Parameters:admin-api-key-sha256`.
