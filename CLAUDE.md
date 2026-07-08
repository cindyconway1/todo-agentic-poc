# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A 3-tier ToDo backend: ASP.NET Core Web API + CSLA business layer + EF Core/SQL Server data layer. C# 12 on .NET 10. Solution file is `ToDo.slnx` (modern XML solution format).

Layout:
- `src/ToDo.Api/` — ASP.NET Core minimal API (entry point: `Program.cs`)
- `src/ToDo.Business/` — CSLA 10 business logic (currently stubbed)
- `src/ToDo.DataAccess/` — EF Core + SQL Server (currently stubbed)
- `tests/ToDo.UnitTests/` — xUnit, no DB required
- `tests/ToDo.IntegrationTests/` — xUnit, **requires a running SQL Server**

## Commands

- Build: `dotnet build --configuration Release`
- Unit tests: `dotnet test tests/ToDo.UnitTests --configuration Release`
- Integration tests: `dotnet test tests/ToDo.IntegrationTests --configuration Release` (needs SQL Server up)
- Run the API: `dotnet run` from `src/ToDo.Api/` (http://localhost:5251 / https://localhost:7075)
- Single test: `dotnet test --filter "FullyQualifiedName~TestName"`

## Conventions

- Namespaces/projects follow `Upward.{Layer}.{Feature}`; use `InternalsVisibleTo` for test access and pin package versions.
- Model DB-backed reference data with **TypeID** reference types, not `enum`s.
- Authentication identity flows through the **CSLA context**; use claim-name constants and named authorization policies.
- Audit through the **EventServices** facade, with traceable reference IDs.
- `Nullable` and `ImplicitUsings` are enabled in all projects — assume nullable reference types and global usings; don't add redundant `using` directives.
- Indentation is spaces (XML files use 2 spaces) per `.editorconfig`.
- OpenAPI is generated to `ToDo.Api.json` on build — do not hand-edit it.

Layer-specific conventions live in `.claude/rules/` (`csla.md` for the Business layer, `api.md` for the API) and load automatically when working in those directories.

## Testing

- **Every change needs a test — and a *stub does not count*.** The DoD test items must exist as real, named, asserting tests; an empty or placeholder test (e.g. `Test1(){}`) is not acceptance. In the PR, map each test to the acceptance criterion it covers. A suite that is green only because it contains no real tests is a red flag, not a pass.
- **Three tiers, by what each catches:**
  - **Unit** (`tests/ToDo.UnitTests`, no DB) — business rules, validation, and pure logic (hashing, command outcomes).
  - **Pipeline smoke** (`tests/ToDo.UnitTests`, no DB, `WebApplicationFactory<Program>`) — **wiring**: DI registration, MVC filters, auth, antiforgery, routing, serialization. These boot the real HTTP pipeline but never touch SQL, so they run in the same no-DB gate you self-verify before a PR. **Any new endpoint or change to pipeline wiring (service registration, filters, auth, middleware) requires a smoke test here.** This is what catches the class of bug that compiles and passes unit tests but 500s at runtime.
  - **Integration** (`tests/ToDo.IntegrationTests`, requires SQL Server, runs in CI) — persistence and data-portal behavior against a real database. Use a **real** SQL Server (throwaway DB per test, migrate then drop); do **not** use the EF Core in-memory provider for persistence assertions — it does not enforce constraints (e.g. the unique email index) and gives false confidence.

## Pull requests

- **Self-verify before opening a PR.** Run `dotnet build --configuration Release` and `dotnet test tests/ToDo.UnitTests --configuration Release`. That unit-test run **includes the DB-free pipeline smoke tests**, so it catches wiring/DI/filter/auth bugs before the PR — not just business logic. If either fails, fix the cause and re-run until both are green — do not open the PR with a known-red build. Only open the PR once both pass, and state in the PR description that the build and unit tests are green. (Integration tests run in CI, which requires SQL Server.)
- **Don't loop indefinitely; report where you were triggered.** If build or unit tests still fail after ~2–3 focused fix attempts, stop and post the error output plus your diagnosis as a comment on the **issue or PR that triggered you**. Because you self-verify *before* opening a PR, this failure usually happens with **no PR yet** — in that case, comment on the originating issue rather than opening a red PR. A capped, well-described failure is more useful than an exhausted turn budget.
- **EF Core migrations are the highest-review-priority artifact.** Call them out explicitly in the PR description so reviewers focus on them.

## Gotchas

- Local development needs SQL Server LocalDB; the dev connection string in `appsettings.Development.json` points at `(localdb)\MSSQLLocalDB`.
- The Business and DataAccess layers are still placeholders (`Class1.cs` stubs); CSLA and EF Core are wired up but unused so far.
