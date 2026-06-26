# Technical Spec — Sport League Director To-Do App

> **Companion documents**
> - `feature.md` — *what & why* (requirements, behavior, acceptance criteria). The source of truth for product behavior.
> - `spec.md` — *how* (this document: architecture, data model, business objects, API contract, security, tests).
> - `implementation-plan.md` — the ordered, ticket-by-ticket build plan derived from this spec.
>
> Where this spec references behavior, the `feature.md` acceptance criteria (AC 1–31) are authoritative. This document maps each requirement onto the architecture established by the pipeline guide.

---

## 1. Context & architecture

This app is built on the harness from the pipeline guide (Parts A–E complete). It is a **3-tier app across two repositories**, coordinated by a generated API contract:

- **`todo-agentic-poc`** (backend repo): `ToDo.Api` (ASP.NET Core controllers + CSLA), `ToDo.Business` (CSLA business objects), `ToDo.DataAccess` (EF Core → SQL Server). Reference flow **Api → Business → DataAccess**; the API never touches the database directly, so business rules always run. Emits the OpenAPI contract as **`ToDo.Api.json`** at the repo root on build.
- **`todo-web`** (frontend repo): Vue 3 + TypeScript + Pinia + Vue Router + Tailwind, Vitest tests. Consumes the backend contract via `openapi-typescript` → `src/api/schema.d.ts` and an `openapi-fetch` client. Tests run against **mocked** API responses.

CSLA runs **in-process** via the local data portal: controllers inject `IDataPortal<T>` and call business objects directly. Three logical tiers, one process. The authenticated `ClaimsPrincipal` flows into `ApplicationContext.User`, so business objects can read the current user.

Targets per the guide's locked decisions: **.NET 10 (LTS) + CSLA 10.1**, **EF Core (SQL Server provider)**, **SQL Server Express LocalDB** locally / throwaway SQL Server container in CI, **controllers** (not minimal APIs) returning typed DTOs with documented status codes so the OpenAPI spec is tight.

---

## 2. Backend solution layout

| Project | Tier | Contains |
|---|---|---|
| `ToDo.Api` | Presentation | Controllers, DTOs, DTO↔business-object mapping, auth/cookie/antiforgery setup, CORS, `Program.cs`. References `ToDo.Business`. |
| `ToDo.Business` | Domain | CSLA business objects, business rules, data-portal methods. References `ToDo.DataAccess`. |
| `ToDo.DataAccess` | Data | `ApplicationDbContext`, EF Core entity configurations, migrations. |
| `ToDo.UnitTests` | Test | xUnit. CSLA rules / business-object behavior. References `ToDo.Business`. |
| `ToDo.IntegrationTests` | Test | xUnit. Persistence + API end-to-end against a throwaway SQL Server. References `ToDo.Api`. |

**Tier rule for ownership/security:** controllers authenticate the request and never trust a client-supplied owner id. Data-portal methods in `ToDo.Business` read the current user from `ApplicationContext.User` (claim `NameIdentifier`) to **stamp `OwnerUserId` on insert** and **filter by `OwnerUserId` on every fetch/update/delete**. A fetch that finds nothing the current user owns returns null → the controller maps that to **404**.

---

## 3. Data model (EF Core)

Keys are `GUID` (or `int` identity — pick one and apply consistently; this spec assumes `Guid`). `OwnerUserId` columns (every table except `User`) are indexed.

### Base entity & audit columns (every table)

Every table carries the same four **audit columns**, plus `OwnerUserId` (except `User`):

| Column | Type | Set on |
|---|---|---|
| `CreateDt` | datetime2 (UTC) | insert |
| `LastUpdateDt` | datetime2 (UTC) | insert **and** every update |
| `CreateUserId` | Guid? (audit reference; **not** an enforced FK) | insert |
| `LastUpdateUserId` | Guid? (audit reference; **not** an enforced FK) | insert **and** every update |

- Defined via an `IAuditable` interface on a shared base entity that all EF Core entities implement.
- **Stamped automatically** in `ApplicationDbContext.SaveChangesAsync` by inspecting the change tracker (Added → all four; Modified → `LastUpdateDt` + `LastUpdateUserId`), pulling the actor from the same current-user accessor used for ownership (§2). No business object or controller sets them by hand.
- The audit user columns are **plain Guid columns, not FKs** — this avoids SQL Server multiple-cascade-path errors and is a standard audit pattern. For **self-registration** (no authenticated actor yet), the new `User` row is stamped with **its own Id** (safe because `Guid` keys are app-generated before insert); columns are nullable to allow this.
- `OwnerUserId` (data isolation) and `CreateUserId` (audit) are **distinct concerns** and usually equal in v1; keep both — they diverge once collaboration arrives.
- Audit columns are **never exposed in API DTOs**.

> The per-table listings below show each table's **domain** columns only; assume the four audit columns above on every one. These date stamps replace the earlier single `CreatedAt`; the sort tiebreak in §7 uses `CreateDt`.

### Entities & columns

**User**
| Column | Type | Constraints |
|---|---|---|
| Id | Guid | PK |
| Email | nvarchar(256) | Required; **unique index, case-insensitive** |
| PasswordHash | nvarchar(max) | Required; Argon2id encoded string |
| *(+ audit columns)* | | see Base entity above; self-stamped on registration |

**League**
| Column | Type | Constraints |
|---|---|---|
| Id | Guid | PK |
| OwnerUserId | Guid | FK → User; required; indexed |
| Name | nvarchar(100) | Required, 1–100 |
| *(+ audit columns)* | | see Base entity above |

**Team**
| Column | Type | Constraints |
|---|---|---|
| Id | Guid | PK |
| OwnerUserId | Guid | FK → User; required; indexed |
| Name | nvarchar(100) | Required, 1–100 |
| LeagueId | Guid? | **Optional tag.** FK → League; `ON DELETE SET NULL` |
| *(+ audit columns)* | | see Base entity above |

**Volunteer**
| Column | Type | Constraints |
|---|---|---|
| Id | Guid | PK |
| OwnerUserId | Guid | FK → User; required; indexed |
| Name | nvarchar(100) | Required, 1–100 |
| LeagueId | Guid? | **Optional tag.** FK → League; `ON DELETE SET NULL` |
| *(+ audit columns)* | | see Base entity above |

**VolunteerTeam** (join — volunteer tagged with one or more teams)
| Column | Type | Constraints |
|---|---|---|
| VolunteerId | Guid | FK → Volunteer; `ON DELETE CASCADE` |
| TeamId | Guid | FK → Team; `ON DELETE CASCADE` |
| | | Composite PK (VolunteerId, TeamId) |
| *(+ audit columns)* | | see Base entity above (applies to the join too) |

**TodoList**
| Column | Type | Constraints |
|---|---|---|
| Id | Guid | PK |
| OwnerUserId | Guid | FK → User; required; indexed |
| ScopeType | tinyint (enum) | Required: 1=League, 2=Team, 3=Volunteer |
| ScopeEntityId | Guid | Required; references the entity identified by ScopeType (validated in business rule, not a DB FK, because it's polymorphic) |
| Name | nvarchar(100) | Required, 1–100 |
| *(+ audit columns)* | | see Base entity above |
| | | Index on (OwnerUserId, ScopeType, ScopeEntityId) |

**TodoItem**
| Column | Type | Constraints |
|---|---|---|
| Id | Guid | PK |
| ListId | Guid | FK → TodoList; required; `ON DELETE CASCADE` |
| OwnerUserId | Guid | FK → User; required; indexed (denormalized for the All-Items query) |
| Title | nvarchar(200) | Required, 1–200 (trimmed) |
| Description | nvarchar(200) | Optional, ≤200 |
| DueDate | date (null) | Optional, date-only |
| IsCompleted | bit | Default 0 |
| CompletedAt | datetime2? | Null until completed |
| *(+ audit columns)* | | see Base entity above; `CreateDt` is the sort tiebreak (§7) |
| | | Index on (ListId, IsCompleted, DueDate); index on (OwnerUserId, IsCompleted, DueDate) for All-Items |

### Delete behavior (matches `feature.md` §5.2, §9)
- **League / Team / Volunteer with lists** → delete is **blocked** in the business layer with a clear error (the controller returns **409 Conflict**). Enforced by a pre-delete check, not by DB cascade.
- **League/Team referenced only as a tag** → delete is allowed; the tag is cleared (`ON DELETE SET NULL` for league tags; `ON DELETE CASCADE` removes `VolunteerTeam` rows for team tags).
- **TodoList** → deleting cascades its items (`ON DELETE CASCADE`).

### Migration notes (highest review priority)
- Each schema change ships as **one EF Core migration in the same PR** as the code that depends on it. Per the backend `CLAUDE.md`, migrations are the highest-review-priority artifact and must be called out explicitly in the PR description.
- The four **audit columns** (§ Base entity) are part of every table's create migration; the auto-stamping lives in `ApplicationDbContext.SaveChangesAsync` (set up in BE-01) so later tables inherit it for free.
- Polymorphic `TodoList.ScopeEntityId` has **no DB foreign key** (it points at one of three tables). Integrity is enforced by a CSLA rule (see §4). Document this in the migration PR so a reviewer doesn't "fix" it by adding an FK.

---

## 4. CSLA business object design

### Conventions (from the backend `CLAUDE.md`)
- Editable objects derive from `BusinessBase<T>`; editable collections from `BusinessListBase<T,C>`; read-only from `ReadOnlyBase<T>` / `ReadOnlyListBase<T,C>`; operations from `CommandBase<T>`.
- Every property registered with `RegisterProperty`. Validation lives in the rules engine via `BusinessRules.AddRule`.
- Data access **only** in data-portal methods (`[Fetch]`, `[Insert]`, `[Update]`, `[Delete]`, `[Execute]`) using the **injected** `ApplicationDbContext`. Objects are invoked via injected `IDataPortal<T>` / `ICommandDataPortal<T>`.
- Every rule → a unit test; every persistence path → an integration test.

### Object inventory

| Object | CSLA base | Purpose / key rules |
|---|---|---|
| `UserEdit` | `BusinessBase` | Registration. Rules: email required + valid format; password required + policy (§5). Insert hashes the password (Argon2id) and enforces uniqueness via the DB unique index; a duplicate surfaces as a domain `DuplicateEmailException` → 409. |
| `LoginCommand` | `CommandBase` | Verifies email + password (Argon2id verify). Returns success + user id/claims on match; **generic failure** otherwise (never reveals whether the email exists). |
| `LeagueEdit` | `BusinessBase` | Name rule (1–100). Delete blocked if lists exist. |
| `LeagueInfoList` / `LeagueInfo` | `ReadOnlyListBase` / `ReadOnlyBase` | Listing leagues for the current user. |
| `TeamEdit` | `BusinessBase` | Name rule; `LeagueId` optional, and if set must reference a league owned by the current user (ownership rule). Delete blocked if lists exist. |
| `TeamInfoList` / `TeamInfo` | read-only | Listing teams (+ league tag). |
| `VolunteerEdit` | `BusinessBase` | Name rule; optional `LeagueId` (ownership rule); child collection of team tags. Each tagged team must be owned by the current user. Delete blocked if lists exist. |
| `VolunteerTeamList` / `VolunteerTeamEdit` | `BusinessListBase` / child `BusinessBase` | The volunteer's team tags. |
| `VolunteerInfoList` / `VolunteerInfo` | read-only | Listing volunteers (+ league tag + team tag ids). |
| `TodoListEdit` | `BusinessBase` | Name rule; `ScopeType` + `ScopeEntityId` required and must reference an entity of that type owned by the current user (polymorphic ownership rule — replaces the missing DB FK). Delete cascades items. |
| `TodoListInfoList` / `TodoListInfo` | read-only | Listing lists. |
| `TodoItemEdit` | `BusinessBase` | Title required + ≤200 (trimmed); Description ≤200; DueDate optional valid date. Insert stamps `OwnerUserId` + `ListId` (list must be owned by user). |
| `CompleteItemCommand` | `CommandBase` | Marks an item complete **one-way**: sets `IsCompleted=true`, `CompletedAt=now`. Rule rejects completing an item not owned by the user, and rejects any attempt to un-complete (no reverse path exists). |
| `DashboardInfo` | `ReadOnlyBase` (with three `ReadOnlyListBase` groups) | Builds the grouped dashboard in one efficient query: Leagues / Teams / People → entity → its lists → **incomplete** items sorted by due date (nulls last). |
| `AllItemsList` / `AllItemInfo` | `ReadOnlyListBase` / `ReadOnlyBase` | All **incomplete** items across all lists for the user, sorted by due date (nulls last), each carrying source list/entity labels. |

### Current-user & ownership enforcement
- Controllers run under cookie auth; `ApplicationContext.User` exposes the `ClaimsPrincipal`. A small helper resolves the current user id from the `NameIdentifier` claim.
- **Insert** stamps `OwnerUserId` from the current user — never from the request body.
- **Fetch/Update/Delete** always filter by `OwnerUserId`. Cross-owner access yields null/empty → 404 at the controller.
- Tag-ownership and scope-ownership rules query the injected context to confirm referenced entities belong to the current user before allowing save.

---

## 5. Authentication & security implementation

### Password hashing — Argon2id
- Hash with **Argon2id**. Recommended library: `Isopoh.Cryptography.Argon2` (produces and verifies an encoded hash string) or `Konscious.Security.Cryptography.Argon2`.
- Starting parameters (OWASP-aligned; tune to the VM): memory **19456 KiB (19 MiB)**, iterations **2**, parallelism **1**, 32-byte hash, 16-byte random salt. Store the full encoded hash in `User.PasswordHash`.
- Plaintext passwords are never logged and never returned by any endpoint.

### Session — Native Cookie pattern (per `feature.md` §6)
- Use ASP.NET Core **`AddAuthentication().AddCookie(...)`**. The framework issues encrypted, signed cookies via the Data Protection API.
- On successful login, the controller calls `SignInAsync` with a `ClaimsPrincipal` containing `NameIdentifier` (user id) and `Name`/`Email`. Logout calls `SignOutAsync`.
- Cookie flags: **`HttpOnly = true`**, **`SecurePolicy = Always`** (`Secure=true`), **`SameSite = Strict`**.
- Unauthenticated API calls return **401** (configure the cookie events so the API does not 302-redirect to a login page).

### CSRF / antiforgery (SPA + cookie auth)
- Configure `AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN")`.
- Expose a `GET /api/auth/antiforgery` endpoint that calls `IAntiforgery.GetAndStoreTokens` to set a **non-HttpOnly `XSRF-TOKEN` cookie** the SPA can read.
- The SPA reads that cookie and echoes the value in the `X-XSRF-TOKEN` header on every state-changing request. Controllers validate via autovalidation / `[ValidateAntiForgeryToken]` on POST/PUT/PATCH/DELETE.

### Local dev cross-origin / cross-scheme note (implementation risk — flag in the auth PR)
The guide runs the API on `https://localhost:7075` and the Vue dev server on `http://localhost:5173`. Under browsers' **schemeful same-site** rules, `http://localhost` ↔ `https://localhost` is treated as cross-site, so a **`SameSite=Strict`** auth cookie may **not** be sent from the dev SPA to the API. Resolve one of:
1. **Vite dev proxy (recommended):** proxy `/api` from the dev server to `https://localhost:7075` so the browser sees a same-origin call. Adjust the frontend to call relative `/api` paths in dev (interacts with the guide's `VITE_API_URL`; document the change).
2. **Serve the dev frontend over HTTPS** (`vite --https` with the trusted dev cert) so both sides share the `https` scheme.
3. Relax to `SameSite=Lax` **in Development only** (keep `Strict` in non-dev).

Also enable **CORS** for the dev origin with `AllowCredentials()` and an explicit origin (not `*`) if calling cross-origin directly (option 2/3). With the proxy (option 1) CORS is unnecessary.

### Authorization
- All endpoints except `register`, `login`, and `antiforgery` require authentication (`[Authorize]` by default, with `[AllowAnonymous]` on the exceptions).
- Per-record ownership is enforced in the business layer (§4). There are no roles in v1.

### Generic auth failures
- Login with an unknown email or a wrong password returns the **same** 401 + generic "invalid email or password" body. Duplicate registration returns **409** "email already in use". (AC 2, 6, 7.)

---

## 6. API contract (controllers + DTOs)

### Conventions
- Controllers return **DTOs**, never CSLA objects, so the OpenAPI document is clean and stable. Annotate every action with `[ProducesResponseType]` for each status code so the generated TypeScript types and status handling are accurate.
- Errors use **`ProblemDetails`** (RFC 7807). Validation failures return **400** with field-level details.
- Dates are serialized as ISO `yyyy-MM-dd` (date-only for `dueDate`).

### DTOs (shapes; names indicative)
- `RegisterRequest { email, password }`, `LoginRequest { email, password }`, `UserDto { id, email }`
- `LeagueDto { id, name }`, `CreateLeagueRequest { name }`, `UpdateLeagueRequest { name }`
- `TeamDto { id, name, leagueId? }`, `CreateTeamRequest { name, leagueId? }`, `UpdateTeamRequest { name, leagueId? }`
- `VolunteerDto { id, name, leagueId?, teamIds[] }`, `CreateVolunteerRequest { name, leagueId?, teamIds[] }`, `UpdateVolunteerRequest { name, leagueId?, teamIds[] }`
- `TodoListDto { id, name, scopeType, scopeEntityId }`, `CreateTodoListRequest { name, scopeType, scopeEntityId }`, `UpdateTodoListRequest { name }`
- `TodoItemDto { id, listId, title, description?, dueDate?, isCompleted, completedAt? }`, `CreateTodoItemRequest { title, description?, dueDate? }`, `UpdateTodoItemRequest { title, description?, dueDate? }`
- `DashboardDto { leagues: GroupDto[], teams: GroupDto[], people: GroupDto[] }` where `GroupDto { entityId, entityName, lists: DashboardListDto[] }` and `DashboardListDto { listId, listName, items: TodoItemDto[] }` (items incomplete, sorted)
- `AllItemDto { id, listId, listName, scopeType, scopeName, title, description?, dueDate? }`

### Endpoints

| Method & route | Auth | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/auth/register` | anon | RegisterRequest | 201 UserDto | 400 validation; 409 duplicate email |
| `POST /api/auth/login` | anon | LoginRequest | 204 + Set-Cookie | 400; 401 generic |
| `POST /api/auth/logout` | yes | — | 204 | 401 |
| `GET  /api/auth/me` | yes | — | 200 UserDto | 401 |
| `GET  /api/auth/antiforgery` | anon | — | 204 + Set-Cookie (XSRF-TOKEN) | — |
| `GET  /api/leagues` | yes | — | 200 LeagueDto[] | 401 |
| `POST /api/leagues` | yes | CreateLeagueRequest | 201 LeagueDto | 400; 401 |
| `GET  /api/leagues/{id}` | yes | — | 200 LeagueDto | 401; 404 |
| `PUT  /api/leagues/{id}` | yes | UpdateLeagueRequest | 200 LeagueDto | 400; 401; 404 |
| `DELETE /api/leagues/{id}` | yes | — | 204 | 401; 404; 409 (has lists) |
| `GET/POST/GET{id}/PUT{id}/DELETE{id} /api/teams` | yes | Team requests | as leagues | + 404 if tagged league not owned |
| `GET/POST/GET{id}/PUT{id}/DELETE{id} /api/volunteers` | yes | Volunteer requests | as leagues | + 404 if tagged league/team not owned |
| `GET  /api/lists` | yes | — | 200 TodoListDto[] | 401 |
| `POST /api/lists` | yes | CreateTodoListRequest | 201 TodoListDto | 400; 401; 404 (scope entity not owned) |
| `GET  /api/lists/{id}` | yes | — | 200 TodoListDto | 401; 404 |
| `PUT  /api/lists/{id}` | yes | UpdateTodoListRequest | 200 TodoListDto | 400; 401; 404 |
| `DELETE /api/lists/{id}` | yes | — | 204 (cascades items) | 401; 404 |
| `GET  /api/lists/{listId}/items` | yes | — | 200 TodoItemDto[] (incomplete, sorted) | 401; 404 |
| `POST /api/lists/{listId}/items` | yes | CreateTodoItemRequest | 201 TodoItemDto | 400; 401; 404 |
| `PUT  /api/items/{id}` | yes | UpdateTodoItemRequest | 200 TodoItemDto | 400; 401; 404 |
| `DELETE /api/items/{id}` | yes | — | 204 | 401; 404 |
| `PATCH /api/items/{id}/complete` | yes | — | 204 (one-way) | 401; 404 |
| `GET  /api/dashboard` | yes | — | 200 DashboardDto | 401 |
| `GET  /api/items/all` | yes | — | 200 AllItemDto[] (incomplete, sorted) | 401 |

> Every backend ticket that adds or changes any row above changes `ToDo.Api.json`. That triggers the contract-regen step in the frontend (§10, and the orchestration steps in `implementation-plan.md`).

---

## 7. Sorting & completion semantics

- **Sort order (everywhere items appear):** ascending by `DueDate`; rows with **null `DueDate` sort last**; tiebreak by `CreateDt` ascending. Implement in the data-portal query (`OrderBy(dueDate == null) .ThenBy(dueDate) .ThenBy(createDt)` or SQL equivalent) so the API returns pre-sorted data. (AC 26, 27.)
- **Completion is one-way:** `PATCH /complete` sets `IsCompleted=true` + `CompletedAt`. No endpoint or business path un-completes or lists completed items. All read queries (list items, dashboard, all-items) filter `IsCompleted == false`. (AC 25; `feature.md` §5.4.)

---

## 8. Validation rules → enforcement mapping

| Rule (`feature.md` §8) | Client (Vue) | Server (CSLA rule) | DB |
|---|---|---|---|
| Email required + format | inline | `UserEdit` rule | — |
| Email unique | — (can't be sure) | insert-time check → 409 | **unique index** |
| Password policy (min 8; upper/lower/digit/special) | inline | `UserEdit` rule | — |
| League/Team/Volunteer name 1–100 | inline | name rule | nvarchar(100) |
| Team `LeagueId` owned by user | — | ownership rule | FK (existence only) |
| Volunteer `LeagueId` / team tags owned by user | — | ownership rule | FKs |
| List scope entity owned by user | — | polymorphic ownership rule | (no FK; rule only) |
| Item `Title` required + ≤200 | inline | rule | nvarchar(200) |
| Item `Description` ≤200 | inline (block past 200) | rule | nvarchar(200) |
| Item `DueDate` valid date / optional | date picker | rule | date (nullable) |

Server is the source of truth; client validation is UX only.

---

## 9. Error & status conventions

| Situation | Status | Body |
|---|---|---|
| Validation failure | 400 | ProblemDetails with field errors |
| Unauthenticated | 401 | generic |
| Bad credentials / unknown email (login) | 401 | generic "invalid email or password" |
| Not found / cross-owner access | 404 | ProblemDetails |
| Duplicate email (register) | 409 | "email already in use" |
| Delete entity that still has lists | 409 | "remove its lists first" |
| Missing/invalid antiforgery token | 400 | antiforgery failure |

---

## 10. Frontend architecture (`todo-web`)

### Structure
```
src/
├─ api/        schema.d.ts (generated) · client.ts (openapi-fetch)
├─ stores/     Pinia: auth, leagues, teams, volunteers, lists, items, dashboard
├─ router/     routes + auth guard
├─ views/      Login, Register, Dashboard, AllItems, EntityManagement, ListDetail
├─ components/ ItemRow (checkbox + due-date), ItemEditor, EntityForm, TagPicker, EmptyState
└─ composables/ useAntiforgery, useApi
```

### Contract & data access
- **`src/api/schema.d.ts` is generated** from `ToDo.Api.json` via `npm run gen:api`. Treat it as the source of truth; **never hand-write request/response shapes** (backend `CLAUDE.md`/frontend `CLAUDE.md` rule).
- All calls go through the `openapi-fetch` client (`src/api/client.ts`), with `credentials: 'include'` so the auth cookie is sent.
- An interceptor/composable reads the `XSRF-TOKEN` cookie and sets the `X-XSRF-TOKEN` header on mutating requests; on **401** it routes to login.

### State & routing
- **Pinia** stores per resource. The `auth` store holds the current user (`GET /api/auth/me` on app load) and drives the router guard.
- **Vue Router** guard: unauthenticated users hitting a protected route are redirected to `/login`; the post-login landing route is the **Dashboard**.

### UI behavior (maps to `feature.md`)
- **Dashboard** (landing): lists grouped into Leagues / Teams / People; each list's incomplete items sorted by due date; empty states. (AC 28, 30.)
- **All Items** view: flattened incomplete items sorted by due date (nulls last), each labeled with its source list/entity. (AC 29.)
- **List detail**: Google-Keep-style checkbox list with a per-item **due-date calendar control**; checking an item calls `/complete` and the item disappears (one-way). (AC 25; `feature.md` §5.3–5.4.)
- **Entity management**: CRUD for leagues/teams/volunteers with tag pickers (team→league; volunteer→league + multiple teams). (AC 13–18.)
- Inline validation mirrors §8 for UX; the server remains authoritative.

### Env / dev wiring
- `.env.development` provides `VITE_API_URL`; reconcile with the cookie cross-scheme note in §5 (proxy or HTTPS dev server recommended).

---

## 11. Test strategy

| Layer | Tool | Covers |
|---|---|---|
| Backend unit | xUnit (`ToDo.UnitTests`) | Every CSLA rule: email/password validation, name length, tag-ownership, scope-ownership, title/description/date rules, one-way completion guard, generic login outcome, Argon2 hash/verify round-trip. |
| Backend integration | xUnit (`ToDo.IntegrationTests`) | Persistence + API against a **throwaway SQL Server** (LocalDB locally / service container in CI). Migrations apply; CRUD round-trips; ownership isolation (cross-user → 404); duplicate email → 409; delete-with-lists → 409; tag clearing on entity delete; sort order incl. nulls-last; completion hides items; auth cookie set with correct flags; antiforgery enforced. Use `WebApplicationFactory`. |
| Frontend | Vitest (mocked API) | Store actions, router guard, antiforgery header injection, form validation UX, dashboard grouping/sort rendering, all-items flatten, completion-hides-item, empty states. **No live backend** — mocks are typed by the generated schema. |

Coverage expectation: each `feature.md` acceptance criterion (AC 1–31) has at least one test at the appropriate layer. Tickets list the ACs they must satisfy.

---

## 12. Non-functional constraints & out of scope

- **Security defaults:** Argon2id hashing; encrypted+signed cookie; `HttpOnly`/`Secure`/`SameSite=Strict`; antiforgery on mutations; per-record ownership isolation; generic auth failures; no plaintext password ever stored, logged, or returned.
- **Forward compatibility:** `OwnerUserId` stays as ownership. Collaboration would later be added via a `ListMembership(ListId, UserId, Role)` table — do not repurpose `OwnerUserId`.
- **Out of scope (v1):** OAuth, MFA, sharing/collaboration, reminders/recurrence, tag-driven filtering, enforced hierarchy/cascade, manual item reordering, viewing/restoring completed items, multi-league membership.

---

## 13. Open assumptions (carried from `feature.md` §11 — confirm before/at ticket time)

1. **Multiple named lists per entity** (vs. one list per entity). This spec and the data model assume **multiple named lists**. Flipping this removes `TodoList.Name` and simplifies the model + the Lists tickets.
2. **Title max length 200** (description is 200 per spec; title was unspecified).
3. **Password policy specifics** (min 8 + four character classes) — pin exact numbers.
4. **Key type** `Guid` vs `int` — assumed `Guid`; apply consistently.
5. **Tag cardinality** — Team→League and Volunteer→League single; Volunteer→Teams many. Confirm no multi-league need.
6. **Local dev cookie wiring** — choose the Vite proxy vs HTTPS dev server vs dev-only `SameSite=Lax` resolution (§5).
