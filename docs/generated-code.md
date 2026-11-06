# Pre-generated Wolverine code

Wolverine builds a small C# class per message handler (and saga) that wires the handler to its middleware, transactions and outbox.
By default it generates and **compiles that code with Roslyn at runtime**: at startup and when a message type is handled for the
first time. In the containers that cost 300-600 MiB of native memory per service API (plan 07 Task 5b: on the k3d cluster
`Microsoft.CodeAnalysis.dll` was loaded in every Wolverine API and their RSS reached 450-940 MiB under the demo seed, almost all of it
glibc malloc arenas outside the capped GC heap).

So each service API commits the generated code under `Internal/Generated/` (compiled into the API like any other source), and the
container images run on it.

## Modes (`Wolverine:TypeLoadMode`)

| Value | Used by | Behaviour |
|---|---|---|
| `Dynamic` (default when unset) | AppHost, integration tests, `dotnet run` | Ignores the committed code; Roslyn compiles at runtime. |
| `Auto` | optional, local experiments | Uses the committed types where they exist, compiles the rest. |
| `Static` | container images (`src/Directory.Build.props` sets `Wolverine__TypeLoadMode=Static`) | Only the committed types; a missing one fails at startup (`AssertAllPreGeneratedTypesExist`). Roslyn never loads. |

The setting is applied to the JasperFx defaults (`CodeGenerationSetup`, BuildingBlocks.Messaging), which Wolverine and Marten (Audit)
both read. Marten 9 generates no code for Audit's documents; only Audit's Wolverine handlers are pre-generated.

## Regenerate after a change

Regenerate whenever a handler, saga, message type, middleware/policy or the DI registration of a handler dependency changes:

```powershell
dotnet build src/Services/<Service>/ProcureAi.<Service>.Api "-t:Build;WriteGeneratedCode"
# all services:
foreach ($s in 'Access','Approvals','Assistant','Audit','Budgets','Invoicing','Notifications','Purchasing','Requests','Suppliers') {
    dotnet build src/Services/$s/ProcureAi.$s.Api -m:4 "-t:Build;WriteGeneratedCode"
}
```

`WriteGeneratedCode` (src/Services/Directory.Build.targets) deletes `Internal/Generated`, then runs the built API as
`ProcureAi.<Service>.Api codegen write` with the OpenAPI step's placeholder settings and `ASPNETCORE_ENVIRONMENT=Production` (the host
is built, never started; nothing connects). Commit the result.

If the build fails because old generated code no longer compiles (a handler was removed or its signature changed), remove it first
in a separate command, then run the command above:

```powershell
dotnet msbuild src/Services/<Service>/ProcureAi.<Service>.Api -t:CleanGeneratedCode
dotnet build src/Services/<Service>/ProcureAi.<Service>.Api "-t:Build;WriteGeneratedCode"
```

The `codegen` run uses the same application assembly as a `Static` start (the API, `Wolverine:GeneratingCode`), so a handler placed in
an API project is generated too, not only the ones in the service's Application assembly and BuildingBlocks.Messaging.

## The guards

**CI (build-time, every push):** the client-drift job of `.github/workflows/ci.yml` builds every API with
`-t:Build;WriteGeneratedCode` and fails when `Internal/Generated` differs from what is committed (new, deleted or changed files, via
`git status --porcelain`) or when an API has no generated code. A stale file that no longer compiles fails the build itself.

**Locally:** `tests/ProcureAi.ArchitectureTests/GeneratedCodeTests` writes a fresh copy of every service's code into a temp folder and compares it
file by file with the committed one (missing or stale code fails), and checks that each API image runs `Static`. Run it after
regenerating:

```powershell
dotnet run --project tests/ProcureAi.ArchitectureTests -- -class "*GeneratedCodeTests"
```

## Caveats

- Code is generated with placeholder Production settings. Wolverine setup (handlers, middleware, policies) must not branch on
  deployment configuration; if it ever must, both variants need generated code, or the image runs the variant generated here.
- Marten 9 writes no code for Audit's documents today. If Marten ever needs generated code (e.g. compiled queries or projections),
  `codegen write` writes it too, and the CI drift check shows the new files until they are committed.
- The generated code inlines `new X(...)` for dependencies Wolverine can construct itself. It is generated with the Production
  registrations; a dependency whose concrete type depends on configuration (e.g. an AI provider) must stay behind an interface that is
  injected or resolved (`AlwaysUseServiceLocationFor<T>()`), never inlined. Today only configuration-independent types are inlined.
