# Feature Spec — Sport League Director To-Do App (PoC)

> **Status:** Draft for review.
> **Purpose:** Proof-of-concept to validate an agentic build pipeline. Optimize for clarity and testability over completeness of features.

---

## 1. Overview

A web app that lets a busy sport league director keep to-do lists organized around the things they manage: **leagues, teams, and volunteers**. A user signs up with an email + password, logs in, and sees their lists grouped by scope, with each list's items sorted by due date. There is also a flattened **"All Items"** view across everything.

This is a single-user-per-account PoC. There is **no collaboration in v1**, but the data model is designed so collaboration can be added later without restructuring.

---

## 2. Goals & Non-Goals

### Goals
- Authenticated users can create and manage scope entities (leagues, teams, volunteers).
- Users can **tag** teams and volunteers with leagues/teams for organizational context.
- Users can create lists scoped to exactly one entity, and manage items within them.
- A grouped dashboard and a flattened "All Items" view, both sorted by due date.
- Secure session handling via HttpOnly cookies; passwords hashed with Argon2.
- A spec concrete enough that an agentic pipeline can implement and test it deterministically.

### Non-Goals (v1)
- OAuth / social login.
- MFA.
- Sharing / collaboration / multi-user access to a list.
- Notifications, reminders, recurring items.
- Mobile-native apps (responsive web only).
- An **enforced** League → Team → Volunteer hierarchy or cascading behavior. Lightweight descriptive **tags** linking these entities are in scope (see §4); they do not cascade or restrict.
- Viewing or restoring completed items (completion is one-way — see §5.4).

---

## 3. Tech Stack (pinned)

### Frontend
- **Vue 3** (Composition API)
- **Pinia** for state management
- **Vue Router** for routing
- **Tailwind CSS** for styling
- **Vite** as the build tool
- **Vitest** for unit/component tests

### Backend
- **ASP.NET Core** (Web API)
- **CSLA .NET** for business objects and business rules
- **EF Core** for persistence (via the CSLA data portal)
- **Unit tests** and **integration tests**

### Architecture notes
- Validation lives in **CSLA business rules** on the business objects where practical, mirrored by lightweight client-side validation in Vue for UX. The server is the source of truth.
- All persistence flows through EF Core behind the CSLA data portal.
- Every persisted record carries an `OwnerUserId`; all reads/writes are filtered to the current authenticated user (see §6).

---

## 4. Domain Model

### Entities
- **User** — an account.
- **League** — a scope entity owned by a user.
- **Team** — a scope entity owned by a user; can be **tagged with one league**.
- **Volunteer** — a scope entity owned by a user ("a person"); can be **tagged with one league and/or one or more teams**.
- **List** — a to-do list scoped to exactly one entity (a League, a Team, or a Volunteer).
- **Item** — a checklist item belonging to a List.

> "Scope entity" = one of {League, Team, Volunteer}. The dashboard groups lists into three buckets by the entity type they belong to. **Tags are descriptive metadata only** in v1 — they don't change scoping, grouping, or access. They may later drive filtering/cross-cutting views.

### Relationships
- A **User** owns many Leagues, Teams, Volunteers, Lists, and Items.
- A **Team** may be tagged with **zero or one League** (owned by the same user).
- A **Volunteer** may be tagged with **zero or one League** and with **zero or more Teams** (all owned by the same user).
- A **List** belongs to exactly one scope entity. (See §11 for the one-vs-many decision.)
- An **Item** belongs to exactly one List.

### Field definitions

**User**
| Field | Type | Notes |
|---|---|---|
| Id | GUID/int (PK) | |
| Email | string | Unique (case-insensitive), required, valid email format. Acts as the login identifier. |
| PasswordHash | string | Argon2id hash. Plaintext is never stored or logged. |
| CreatedAt | datetime (UTC) | |

**League**
| Field | Type | Notes |
|---|---|---|
| Id | GUID/int (PK) | |
| OwnerUserId | FK → User | Required. |
| Name | string | Required, 1–100 chars. |
| CreatedAt | datetime (UTC) | |

**Team**
| Field | Type | Notes |
|---|---|---|
| Id | GUID/int (PK) | |
| OwnerUserId | FK → User | Required. |
| Name | string | Required, 1–100 chars. |
| LeagueId | FK → League (nullable) | **Optional tag.** Links the team to one league owned by the same user. Descriptive only; no cascade. |
| CreatedAt | datetime (UTC) | |

**Volunteer**
| Field | Type | Notes |
|---|---|---|
| Id | GUID/int (PK) | |
| OwnerUserId | FK → User | Required. |
| Name | string | Required, 1–100 chars. |
| LeagueId | FK → League (nullable) | **Optional tag.** Links the volunteer to one league owned by the same user. Descriptive only. |
| CreatedAt | datetime (UTC) | |

**VolunteerTeam** (join table — volunteer tagged with one or more teams)
| Field | Type | Notes |
|---|---|---|
| VolunteerId | FK → Volunteer | Required. |
| TeamId | FK → Team | Required. Must be owned by the same user. |
| | | Composite PK (VolunteerId, TeamId). |

> **Cardinality note:** Per current requirements, Team→League and Volunteer→League are **single** (one league each). If you later want a team/volunteer in multiple leagues, these become many-to-many join tables. Volunteer→Team is already many-to-many.

**List**
| Field | Type | Notes |
|---|---|---|
| Id | GUID/int (PK) | |
| OwnerUserId | FK → User | Required. Denormalized for fast scoping/filtering. |
| ScopeType | enum {League, Team, Volunteer} | Required. Identifies which bucket the list belongs to. |
| ScopeEntityId | FK → (League/Team/Volunteer) | Required. Points at the specific entity. The (ScopeType, ScopeEntityId) pair must reference an entity owned by the same user. |
| Name | string | Required, 1–100 chars. |
| CreatedAt | datetime (UTC) | |

> **Implementation note:** Two acceptable patterns for the polymorphic scope reference — (a) `ScopeType` + `ScopeEntityId` as above, with referential integrity validated in a CSLA rule; or (b) three nullable FKs (`LeagueId`, `TeamId`, `VolunteerId`) with a check constraint that exactly one is non-null. Pattern (a) is preferred for cleaner querying.

**Item**
| Field | Type | Notes |
|---|---|---|
| Id | GUID/int (PK) | |
| ListId | FK → List | Required. |
| OwnerUserId | FK → User | Required. Denormalized for the "All Items" query. |
| Title | string | Required, 1–200 chars (trimmed). |
| Description | string | Optional, max 200 chars. |
| DueDate | date (no time) | Optional. Calendar/date picker. Date-only. |
| IsCompleted | bool | Default false. Completed items are hidden from all views and cannot be viewed or restored (see §5.4). |
| CompletedAt | datetime (UTC) | Nullable. Set when IsCompleted becomes true. |
| CreatedAt | datetime (UTC) | |

---

## 5. Functional Requirements

### 5.1 Accounts & Authentication
- A visitor can **register** with an email and password. Email is the unique identifier.
- Email must be unique (case-insensitive) and a valid email format.
- Password must meet the policy in §8.
- Passwords are hashed with **Argon2id** before storage.
- A registered user can **log in** with email + password and **log out**.
- A logged-in user can retrieve their own profile (at minimum, their email) for display.
- No OAuth, no MFA in v1.

### 5.2 Scope Entities (Leagues, Teams, Volunteers)
- An authenticated user can **create, read, update, and delete** their own leagues, teams, and volunteers.
- A user can only see and modify entities they own.
- **Tagging:**
  - A team can be tagged with **one league** (optional).
  - A volunteer can be tagged with **one league** (optional) and/or **one or more teams** (optional).
  - All tagged references must point at entities owned by the same user.
  - Tags are descriptive; removing a tag (or untagging) has no cascading effect on lists or items.
- Deleting an entity that still has lists: **blocked** with a clear error in v1 (safer for a PoC). See §11. Deleting an entity that is only referenced as a *tag* (e.g., deleting a league that a team is tagged with) simply clears that tag.

### 5.3 Lists
- An authenticated user can **CRUD** their own lists.
- Each list is scoped to exactly one entity (a specific league, team, or volunteer) owned by the same user.
- A list is rendered as a **standard checkbox list** (Google Keep / Google Notes style) with a per-item **due-date calendar control**.
- Only the authenticated owner can CRUD a list (see §6).
- Deleting a list cascade-deletes its items.

### 5.4 Items
- An authenticated user can **CRUD** items within their own lists.
- Each item has a **required title**, an **optional description** (≤200 chars), and an **optional due date**.
- Checking an item marks it **completed**. **Completion is one-way:** completed items are immediately and permanently **hidden** from all views (list, dashboard, "All Items"). In v1 there is **no UI or API to view, list, un-complete, or restore** completed items. `CompletedAt` is recorded for future use.
- Within any view, items are **sorted ascending by due date**; items **with no due date sort to the bottom** (after all dated items). Tiebreak among equal/empty dates by `CreatedAt` ascending.

### 5.5 Dashboard (post-login landing)
- On login, the user lands on a dashboard showing their lists **grouped into three buckets: Leagues, Teams, People (Volunteers)**.
- Within each bucket, lists are shown under their owning entity.
- Within each list, items are sorted by due date per §5.4. Completed items are hidden.
- Empty states are shown when there are no entities, no lists, or no items (see §9).

### 5.6 "All Items" View
- A dedicated **All Items** view flattens **every non-completed item across all lists, all entities, and all scope types** into a single list.
- Sorted ascending by due date; no-due-date items at the bottom (per §5.4).
- Each row should indicate which list/entity the item belongs to (for context, since scope is flattened away).

---

## 6. Security Requirements

### Authentication transport
- Sessions are carried in an **HttpOnly cookie** (never in `localStorage`/`sessionStorage`) to mitigate XSS token theft.
- Implement in ASP.NET Core using the **Native Cookie pattern**: use `AddCookie` authentication. ASP.NET Core issues encrypted, signed session cookies via the Data Protection API and handles their validation automatically.

### Cookie / CSRF constraints
- `HttpOnly = true`
- `Secure = true`
- `SameSite = Strict`
- **CSRF / antiforgery mitigation** configured (ASP.NET Core antiforgery tokens for state-changing requests, given cookie-based auth).

### Authorization & data isolation
- All endpoints except register/login require authentication.
- Every read and write is scoped to the **current user's `OwnerUserId`**. A user must never be able to read or modify another user's entities, lists, or items.
- Cross-owner access attempts return **404** (chosen over 403 to avoid leaking existence), applied consistently.

### Credentials
- Passwords hashed with **Argon2id**; never logged, never returned by any API.
- Login failures use a **generic message** that does not reveal whether the email exists (see §9).

### Forward compatibility (collaboration later)
- Keep `OwnerUserId` as ownership now. Collaboration in a future version should be added via a separate membership/ACL table (e.g., `ListMembership(ListId, UserId, Role)`) layered on top — **do not** repurpose `OwnerUserId` for sharing.

---

## 7. API Contract (suggested)

REST over JSON. All routes are owner-scoped server-side. State-changing routes require an antiforgery token.

**Auth**
- `POST /api/auth/register` → create account
- `POST /api/auth/login` → set auth cookie
- `POST /api/auth/logout` → clear auth cookie
- `GET  /api/auth/me` → current user (email)

**Scope entities** (repeat for `leagues`, `teams`, `volunteers`)
- `GET    /api/leagues`
- `POST   /api/leagues`
- `GET    /api/leagues/{id}`
- `PUT    /api/leagues/{id}` → includes setting/clearing tags (e.g., a team's `LeagueId`, a volunteer's `LeagueId` and team list)
- `DELETE /api/leagues/{id}`

**Lists**
- `GET    /api/lists` → flat list (optionally `?scopeType=`)
- `GET    /api/dashboard` → lists grouped by Leagues / Teams / People, items pre-sorted, completed excluded
- `POST   /api/lists`
- `GET    /api/lists/{id}`
- `PUT    /api/lists/{id}`
- `DELETE /api/lists/{id}`

**Items**
- `GET    /api/lists/{listId}/items` → non-completed, sorted by due date
- `POST   /api/lists/{listId}/items`
- `PUT    /api/items/{id}`
- `DELETE /api/items/{id}`
- `PATCH  /api/items/{id}/complete` → mark completed (one-way; no un-complete)
- `GET    /api/items/all` → flattened "All Items", non-completed, sorted by due date

---

## 8. Validation Rules

### Account creation
- **Email:** required; valid email format; unique (case-insensitive).
- **Password:** required. Proposed policy (adjust as needed):
  - Minimum length **8** (max 128).
  - At least one **uppercase**, one **lowercase**, one **digit**, and one **special character**.
- Validation enforced server-side (CSLA business rules) and mirrored client-side for UX.

### Scope entities & tags
- `Name` required, 1–100 chars (league, team, volunteer).
- A team's `LeagueId`, if set, must reference a league owned by the current user.
- A volunteer's `LeagueId` and any tagged team, if set, must reference entities owned by the current user.

### List
- `Name` required, 1–100 chars.
- `ScopeType` + `ScopeEntityId` required and must reference an entity owned by the current user.

### Item
- `Title` required; non-empty after trimming; ≤200 chars.
- `Description` optional; ≤200 chars.
- `DueDate` optional; if provided, must be a **valid calendar date**. (Past dates are allowed — they represent overdue items.)

---

## 9. Edge Cases & Error Handling

| Scenario | Expected behavior |
|---|---|
| **Duplicate signup** (email already exists) | Reject with a clear "email already in use" error; no account created. |
| **Login: no such account** | Reject with a **generic** "invalid email or password" message (do not reveal the email is unknown). |
| **Login: wrong password** | Same generic "invalid email or password" message. |
| **Signup missing email or password** | Reject with field-level validation errors; nothing persisted. |
| **Password fails policy** | Reject with a message describing the unmet requirements. |
| **Item with no title** | Reject; `Title` is required. Inline validation, item not saved. |
| **Description > 200 chars** | Reject (or block input past 200 in the UI) with a clear "max 200 characters" message. |
| **Invalid due date** | Reject with a "please enter a valid date" message; the date picker should prevent malformed input. |
| **Tagging an entity you don't own** | Reject (404); tag not applied. |
| **Deleting a league/team used only as a tag** | Allowed; the tag is cleared from any team/volunteer that referenced it. |
| **Empty states** | Friendly empty state for: no entities yet, entity with no lists, list with no (incomplete) items, "All Items" with nothing due. |
| **Accessing another user's data** | Treated as not found (404); never returns another user's data. |
| **Deleting an entity that still has lists** | Blocked with a clear message in v1 (see §5.2). |

---

## 10. Acceptance Criteria (draft — please review for completeness)

Written as Given/When/Then. Each is intended to be directly testable (Vitest on the front end, unit + integration tests on the back end).

### Authentication & accounts
1. **Register success** — Given a valid, unused email and a policy-compliant password, when I register, then an account is created with an Argon2id password hash and I can subsequently log in.
2. **Register duplicate** — Given an email already registered, when I register with that email, then registration is rejected with an "email already in use" error and no second account is created.
3. **Register missing fields** — Given a missing email or missing password, when I submit, then registration is rejected with field-level validation errors.
4. **Register weak password** — Given a password that violates the policy, when I submit, then registration is rejected with a message indicating the unmet requirements.
5. **Login success** — Given valid credentials, when I log in, then an auth cookie is set with `HttpOnly`, `Secure`, and `SameSite=Strict`, and I land on the dashboard.
6. **Login unknown email** — Given an email with no account, when I log in, then I get a generic "invalid email or password" error (existence is not revealed).
7. **Login wrong password** — Given a correct email and wrong password, when I log in, then I get the same generic "invalid email or password" error.
8. **Logout** — Given I am logged in, when I log out, then the auth cookie is cleared and protected routes return 401.
9. **No token in browser storage** — Given a logged-in session, then no auth token is present in `localStorage` or `sessionStorage`.

### Authorization & isolation
10. **Auth required** — Given I am unauthenticated, when I call any list/item/entity endpoint, then I receive 401.
11. **Owner isolation** — Given user A owns a record, when user B requests it, then user B receives 404 and never sees A's data.
12. **CSRF protection** — Given cookie-based auth, when a state-changing request is made without a valid antiforgery token, then it is rejected.

### Scope entities & tags
13. **CRUD entities** — Given I am authenticated, when I create/read/update/delete a league, team, or volunteer, then the change persists and is visible only to me.
14. **Delete blocked** — Given an entity that still has lists, when I delete it, then the delete is blocked with a clear message.
15. **Tag team with league** — Given a team and a league I own, when I tag the team with the league, then the association persists and is reflected when I read the team.
16. **Tag volunteer with league and teams** — Given a volunteer, a league, and one or more teams I own, when I tag the volunteer, then it persists with the single league and all selected teams.
17. **Tag ownership integrity** — Given a league or team I do **not** own, when I attempt to tag a team/volunteer with it, then the request is rejected (404) and no tag is applied.
18. **Delete tagged-only entity clears tag** — Given a league tagged on a team but with no lists, when I delete the league, then it is deleted and the team's league tag is cleared.

### Lists
19. **CRUD lists** — Given I am authenticated, when I create/read/update/delete a list scoped to one of my entities, then the change persists.
20. **Scope integrity** — Given I try to create a list scoped to an entity I don't own, then the request is rejected (404).

### Items
21. **Create item requires title** — Given a new item with no title, when I save, then it is rejected; given a valid title, it saves.
22. **Description limit** — Given a description longer than 200 chars, when I save, then it is rejected with a max-length error.
23. **Optional fields** — Given an item with only a title (no description, no due date), when I save, then it persists successfully.
24. **Invalid date rejected** — Given an invalid/malformed due date, when I save, then it is rejected with a validation error.
25. **Complete hides item permanently** — Given an incomplete item, when I check it complete, then `IsCompleted=true`, `CompletedAt` is set, the item no longer appears in any view, and there is no way to view or restore it.

### Sorting & views
26. **Sort by due date** — Given a list with items having various due dates, when I view it, then items appear ascending by due date.
27. **No-date items last** — Given items with and without due dates, when I view any list, then dated items appear first (ascending) and undated items appear at the bottom.
28. **Dashboard grouping** — Given lists across leagues, teams, and volunteers, when I open the dashboard, then lists are grouped into Leagues / Teams / People, each list's items sorted per the rules and completed items hidden.
29. **All Items flatten** — Given items across multiple lists and scope types, when I open "All Items", then every non-completed item appears in a single list sorted by due date (undated last), each labeled with its source list/entity.

### Empty states
30. **Empty dashboard** — Given a brand-new account with no entities, when I open the dashboard, then a friendly empty state is shown.
31. **Empty list / All Items** — Given a list (or All Items) with no incomplete items, when I view it, then an appropriate empty state is shown.

---

## 11. Open Questions / Assumptions to Confirm

1. **One list per entity, or many?** This spec assumes an entity can have **multiple named lists**. If you want exactly one list per entity, lists lose their own `Name` (inherit the entity name) and the model simplifies. → *Confirm.*
2. **List `Name`** — included because of the assumption above. Remove if one-list-per-entity. → *Confirm.*
3. **Entity delete behavior** — assumed **block delete** when lists exist. Alternative is cascade delete. → *Confirm.*
4. **Title max length** — set to 200 (description is 200 per your spec; title wasn't specified). → *Confirm.*
5. **Password policy specifics** — proposed min 8 + character classes; you said "n characters, special characters, etc." Pin the exact numbers. → *Confirm.*
6. **Tag cardinality** — Team→League and Volunteer→League are modeled as **single** league each (per "tag with a league"). Volunteer→Teams is many. Confirm you don't need a team/volunteer in multiple leagues. → *Confirm.*
7. **Cross-owner response code** — chosen **404** over 403 for non-leaking. → *Confirm.*

### Resolved (previously open)
- **League→Team hierarchy:** modeled as descriptive **tags** (team↔league, volunteer↔league, volunteer↔teams), not an enforced hierarchy.
- **Auth pattern:** **Native Cookie pattern** (`AddCookie`) only.
- **Completed items:** completion is **one-way**; no viewing or restoring in v1.

---

## 12. Out of Scope / Future

- Collaboration / sharing of lists across users (data model leaves room via a future membership/ACL table).
- OAuth, MFA.
- Reminders / notifications / recurring items.
- Filtering or cross-cutting views driven by tags (tags are descriptive only in v1).
- Enforced League → Team → Volunteer hierarchy with cascading.
- Item ordering beyond due-date sort (manual drag-reorder).
- Viewing, archiving, or restoring completed items.
- Multi-league membership for teams/volunteers.
