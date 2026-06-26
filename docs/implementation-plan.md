# Implementation Plan — Sport League Director To-Do App

> Derived from `spec.md` and `feature.md`. Built for the pipeline guide's **gated, single-agent, human-orchestrated** flow: the agent works at **PR level within one repo**; you orchestrate across repos. Assumes the harness (guide Parts A–E) is complete: both repos, project skeletons, `CLAUDE.md`, CI, the two Claude workflows, `/health`, and the contract pipeline are all verified.

---

## How to read this plan

**Contract-first ordering.** A full-stack feature is sequenced: backend ticket → review/merge (the contract `ToDo.Api.json` updates) → **regenerate the contract** in `todo-web` (`npm run gen:api`, commit) → frontend ticket(s) → review/merge. Frontend work for a feature never starts before the backend contract for it exists.

**Ticket types**
- `BE-xx` — backend repo (`todo-agentic-poc`). Agent implements API + EF migration **together** in one PR.
- `FE-xx` — frontend repo (`todo-web`). Agent implements UI against the **regenerated** types, tests mock the API.
- `ORCH-x` — **your** manual step (no agent): regenerate/commit the contract, sanity-check, merge.

**Standing rules for every ticket** (state these in each agent prompt; they're in `CLAUDE.md` too)
- Every change ships with tests: CSLA rules → unit tests; persistence/API → integration tests; frontend → Vitest with mocked, schema-typed responses.
- **Backend:** any schema change is one EF Core migration in the same PR, and **the PR description must call the migration out explicitly** (highest review priority).
- **Backend:** controllers return DTOs with `[ProducesResponseType]` for every status code so the OpenAPI contract stays tight. Never trust a client-supplied `OwnerUserId`.
- **Frontend:** never hand-write request/response shapes — import from the generated `src/api/schema.d.ts`. Tests never call a live backend.
- Each ticket names the `feature.md` acceptance criteria (AC #) it must satisfy; the agent's tests should cover them.

**Grouping flexibility.** BE-03/04/05 (entities) may be merged into one PR, and FE-02 likewise, if you prefer fewer, larger reviews. They're split here for reviewability and because the entities have a build order (Team references League; Volunteer references League + Team).

---

## Critical path (at a glance)

```
BE-01 ─► BE-02 ─► ORCH-A ─► FE-01
                     │
                     ├─► BE-03 ─► BE-04 ─► BE-05 ─► ORCH-B ─► FE-02
                     │
                     └─► BE-06 ─► BE-07 ─► BE-08 ─► ORCH-C ─► FE-03 ─► FE-04 ─► FE-05
```
Auth (BE-02) gates everything because all endpoints require an authenticated, owned context. Within the backend, entities (BE-03→05) and lists/items (BE-06→08) can proceed once auth is merged; lists depend on entities existing, items on lists, dashboard/all-items on items.

---

## Phase 0 — Backend foundation

### BE-01 · Backend foundation: DbContext, ownership plumbing, conventions
- **Repo:** backend · **Depends on:** harness
- **Goal:** stand up the data + cross-cutting plumbing everything else builds on, with no domain endpoints yet.
- **Scope (in):** `ApplicationDbContext` in `ToDo.DataAccess` wired to the `Default` connection string and registered for DI/data portal; base entity convention (`OwnerUserId` + the **audit columns** `CreateDt`/`LastUpdateDt`/`CreateUserId`/`LastUpdateUserId` via an `IAuditable` base entity); **automatic audit stamping in `ApplicationDbContext.SaveChangesAsync`** (Added → all four; Modified → the two LastUpdate fields), using the current-user accessor and falling back to self-Id for `User` self-registration; current-user accessor reading `NameIdentifier` from `ApplicationContext.User`; global exception → `ProblemDetails` mapping; DTO + `[ProducesResponseType]` conventions documented in code; an initial (empty or near-empty) EF migration + `dotnet ef database update` path verified.
- **Scope (out):** any entity tables, any business objects, any auth.
- **Contract impact:** none (no new endpoints).
- **Migration:** initial scaffold; call out in PR.
- **Tests:** integration test that migrations apply to a throwaway DB and the context connects; a test proving `SaveChangesAsync` stamps `CreateDt`/`CreateUserId` on insert and `LastUpdateDt`/`LastUpdateUserId` on update.
- **AC:** foundation for all; none directly.

---

## Phase 1 — Authentication

### BE-02 · Auth: accounts, Argon2id, cookie session, antiforgery
- **Repo:** backend · **Depends on:** BE-01
- **Goal:** a user can register, log in, log out, and read their profile; sessions ride a secure cookie; mutations are antiforgery-protected.
- **Scope (in):** `User` entity + migration (unique, case-insensitive email index); Argon2id hashing service; `UserEdit` (email/password rules, insert hashes + enforces uniqueness → 409) and `LoginCommand` (Argon2 verify, generic failure); `AddCookie` auth with `HttpOnly`/`Secure`/`SameSite=Strict` and 401-not-redirect for the API; antiforgery (`X-XSRF-TOKEN` header) + `GET /api/auth/antiforgery`; `AuthController` (`register`, `login`, `logout`, `me`); CORS for the dev origin **or** the Vite-proxy decision documented (spec §5).
- **Contract impact:** **+** auth endpoints, `UserDto`, request DTOs.
- **Migration:** Users table — call out in PR.
- **Tests:** unit — email format, password policy, hash/verify round-trip, generic login outcome; integration — register success, duplicate → 409, login sets cookie with correct flags, `me` requires auth (401), antiforgery rejects missing token.
- **AC:** 1, 2, 3, 4, 5, 6, 7, 8, 12.

### ORCH-A · Regenerate the contract for auth
- **Your step.** In `todo-web`: `npm run gen:api`, confirm TypeScript compiles, commit `schema.d.ts`. (Per guide: the local `gen:api` reads the side-by-side `../todo-agentic-poc/ToDo.Api.json`.)

### FE-01 · Frontend foundation + auth UI
- **Repo:** frontend · **Depends on:** ORCH-A
- **Goal:** users can register/log in/log out; the app knows who's logged in and guards routes.
- **Scope (in):** `auth` Pinia store (loads `me` on startup); Login + Register views with inline validation; logout; Vue Router guard (unauth → `/login`, post-login landing = Dashboard placeholder); `credentials: 'include'` on the client; antiforgery composable (read `XSRF-TOKEN` cookie → `X-XSRF-TOKEN` header); 401 → redirect to login; reconcile dev cookie wiring (proxy/HTTPS) per spec §5.
- **Contract impact:** consumes only.
- **Tests:** Vitest (mocked) — store login/logout/me, guard redirects, antiforgery header set on mutations, register/login form validation, generic error shown on bad login.
- **AC:** 1–9 (UI side), 12.

---

## Phase 2 — Scope entities & tags

### BE-03 · Leagues CRUD
- **Repo:** backend · **Depends on:** BE-02
- **Scope (in):** `LeagueEdit` + read-only list; `LeaguesController` (list/create/get/update/delete); delete blocked if lists exist (→ 409, will be exercisable once lists land); migration (Leagues + `OwnerUserId` index).
- **Contract impact:** **+** league endpoints + DTOs.
- **Migration:** Leagues — call out in PR.
- **Tests:** unit — name rule; integration — CRUD round-trip, ownership isolation (cross-user → 404).
- **AC:** 11, 13.

### BE-04 · Teams CRUD (+ league tag)
- **Repo:** backend · **Depends on:** BE-03
- **Scope (in):** `TeamEdit` with optional `LeagueId` + ownership rule (tagged league must be owned); `TeamsController`; migration (Teams + `LeagueId` FK `ON DELETE SET NULL`); deleting a tagged league clears the tag.
- **Contract impact:** **+** team endpoints + DTOs.
- **Migration:** Teams + FK — call out in PR.
- **Tests:** unit — tag-ownership rule; integration — tag set/clear, tag-not-owned → 404, delete-league-clears-team-tag.
- **AC:** 13, 15, 17, 18.

### BE-05 · Volunteers CRUD (+ league tag + team tags)
- **Repo:** backend · **Depends on:** BE-04
- **Scope (in):** `VolunteerEdit` with optional `LeagueId` and a child collection of team tags; `VolunteerTeam` join table; ownership rules for league + each team; `VolunteersController`; migration (Volunteer + VolunteerTeam, cascade on join).
- **Contract impact:** **+** volunteer endpoints + DTOs (`teamIds[]`).
- **Migration:** Volunteer + VolunteerTeam — call out in PR.
- **Tests:** integration — tag with one league + multiple teams, tag-not-owned → 404, delete-team removes join rows.
- **AC:** 13, 16, 17.

### ORCH-B · Regenerate the contract for entities
- **Your step.** `npm run gen:api` in `todo-web`, verify compile, commit.

### FE-02 · Entity management UI (leagues / teams / volunteers + tagging)
- **Repo:** frontend · **Depends on:** ORCH-B
- **Scope (in):** stores + views for CRUD of leagues, teams, volunteers; `TagPicker` for team→league and volunteer→(league + multiple teams); inline validation; empty states for "no entities yet".
- **Tests:** Vitest (mocked) — CRUD flows, tag selection persists in requests, validation UX.
- **AC:** 13, 15, 16, 17, 18, 30 (entity empty state).

---

## Phase 3 — Lists, items, and views

### BE-06 · Lists CRUD (scoped to an entity)
- **Repo:** backend · **Depends on:** BE-05
- **Scope (in):** `TodoListEdit` with `ScopeType` + `ScopeEntityId` and the **polymorphic ownership rule** (no DB FK — document this); `ListsController`; delete cascades items; migration (TodoList + indexes). Re-confirm the "multiple lists per entity vs one" assumption (spec §13.1) before building.
- **Contract impact:** **+** list endpoints + DTOs.
- **Migration:** TodoList — call out in PR; note the deliberate absence of an FK on `ScopeEntityId`.
- **Tests:** unit — scope-ownership rule; integration — create scoped to owned entity, scope-not-owned → 404, cross-user isolation → 404.
- **AC:** 11, 19, 20.

### BE-07 · Items CRUD + one-way completion + sorting
- **Repo:** backend · **Depends on:** BE-06
- **Scope (in):** `TodoItemEdit` (title required ≤200, description ≤200, optional valid `DueDate`); `CompleteItemCommand` (one-way; rejects un-complete); item endpoints (`list items` incomplete+sorted, create/update/delete, `PATCH /complete`); sort = due date asc, **nulls last**, tiebreak `CreateDt`; reads filter `IsCompleted=false`; migration (TodoItem + sort/owner indexes).
- **Contract impact:** **+** item endpoints + DTOs.
- **Migration:** TodoItem — call out in PR.
- **Tests:** unit — title/description/date rules, one-way completion guard; integration — CRUD, complete hides item and cannot be undone, sort order incl. nulls-last, invalid date → 400, description > 200 → 400, missing title → 400.
- **AC:** 21, 22, 23, 24, 25, 26, 27.

### BE-08 · Dashboard + All Items read models
- **Repo:** backend · **Depends on:** BE-07
- **Scope (in):** `DashboardInfo` read model (`GET /api/dashboard`) grouping lists into Leagues/Teams/People → entity → lists → incomplete items sorted; `AllItemsList` (`GET /api/items/all`) flattening incomplete items across all lists, sorted, each labeled with source list/entity. Efficient queries (avoid N+1).
- **Contract impact:** **+** dashboard + all-items endpoints + DTOs.
- **Migration:** none (read-only over existing tables).
- **Tests:** integration — grouping correctness, completed excluded, sort incl. nulls-last, all-items flatten + source labels.
- **AC:** 28, 29.

### ORCH-C · Regenerate the contract for lists/items/views
- **Your step.** `npm run gen:api` in `todo-web`, verify compile, commit.

### FE-03 · Lists + Items UI
- **Repo:** frontend · **Depends on:** ORCH-C
- **Scope (in):** list detail view as a Google-Keep-style checkbox list; `ItemRow` (checkbox + due-date calendar control), `ItemEditor` (title/description/date with inline validation); create/update/delete; checking an item calls `/complete` and removes it from view (one-way); items rendered in the API's sorted order; per-list empty state.
- **Tests:** Vitest (mocked) — render sorted (nulls last), complete-hides-item, validation (no title, >200 desc, bad date), CRUD flows.
- **AC:** 21–27, 31 (empty list).

### FE-04 · Dashboard + All Items views
- **Repo:** frontend · **Depends on:** FE-03
- **Scope (in):** Dashboard as the post-login landing: lists grouped into Leagues/Teams/People, each list's items sorted, completed hidden; All Items flattened view sorted with source labels; empty states for both.
- **Tests:** Vitest (mocked) — grouping render, all-items flatten + labels, sort, empty dashboard/all-items.
- **AC:** 28, 29, 30, 31.

### FE-05 · Polish (optional)
- **Repo:** frontend · **Depends on:** FE-04
- **Scope (in):** consistent empty/loading/error states across the app, validation-message consistency, navigation between Dashboard / All Items / entity management / list detail.
- **Tests:** Vitest (mocked) — loading/error/empty states.
- **AC:** 30, 31 (consistency).

---

## Order summary

| # | Ticket | Repo | Changes contract? | Migration? |
|---|---|---|---|---|
| 1 | BE-01 Foundation | backend | no | initial |
| 2 | BE-02 Auth | backend | yes | Users |
| 3 | ORCH-A Regenerate | frontend | — | — |
| 4 | FE-01 Auth UI | frontend | consumes | — |
| 5 | BE-03 Leagues | backend | yes | Leagues |
| 6 | BE-04 Teams (+tag) | backend | yes | Teams + FK |
| 7 | BE-05 Volunteers (+tags) | backend | yes | Volunteer + join |
| 8 | ORCH-B Regenerate | frontend | — | — |
| 9 | FE-02 Entity UI | frontend | consumes | — |
| 10 | BE-06 Lists | backend | yes | TodoList |
| 11 | BE-07 Items + completion | backend | yes | TodoItem |
| 12 | BE-08 Dashboard + All Items | backend | yes | none |
| 13 | ORCH-C Regenerate | frontend | — | — |
| 14 | FE-03 Lists + Items UI | frontend | consumes | — |
| 15 | FE-04 Dashboard + All Items UI | frontend | consumes | — |
| 16 | FE-05 Polish (optional) | frontend | consumes | — |

---

## Before you start ticketing — confirm these (spec §13)

1. **Multiple named lists per entity** vs one list per entity (changes BE-06 + the data model).
2. Password policy exact numbers.
3. Key type (`Guid` assumed).
4. Local-dev cookie wiring choice (Vite proxy / HTTPS dev server / dev-only `SameSite=Lax`) — settle in BE-02/FE-01.
5. Whether to keep BE-03/04/05 (and FE-02) split or merge into single PRs.
