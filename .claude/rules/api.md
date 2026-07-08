---
description: API-layer conventions (controllers, error model, mapping)
paths:
  - src/ToDo.Api/**
---

# API layer conventions

## Controllers

- Keep controllers **thin** — delegate logic to a scoped service layer, not the controller body.
- Name actions with descriptive **PascalCase** verbs.
- Apply a consistent attribute stack (routing, auth, response types) across actions.

## Pipeline wiring

- **MVC filter attributes need their services registered.** `[ValidateAntiForgeryToken]` / `[AutoValidateAntiforgeryToken]` depend on `ValidateAntiforgeryTokenAuthorizationFilter`, which is registered by **`AddControllersWithViews()`** — **not** by `AddControllers()`. Using `AddControllers()` with those attributes throws **HTTP 500** at request time (missing service), not at startup. `AddAntiforgery()` registers the `IAntiforgery` service, which is a *separate* concern from the MVC filter.
- **Cover pipeline wiring with a DB-free smoke test.** Every new endpoint or change to service registration, filters, auth, or middleware gets a `WebApplicationFactory<Program>` smoke test in `tests/ToDo.UnitTests` (see CLAUDE.md → Testing). Wiring bugs compile and pass unit tests; only a booted pipeline catches them.

## Models and mapping

- Expose **POCO models** at the API boundary, not business objects.
- Map with **AutoMapper** using direction-specific profiles (separate request→domain and domain→response).

## Error model

- Return **422** for validation failures with the shape `{ id, message, errors[], warnings[] }`.
- **Sanitize SQL exceptions** before returning them — surface a reference ID, never raw SQL/exception detail.

## OpenAPI

- Return **typed DTOs** with **documented status codes** (declare every response type via `[ProducesResponseType]`/`Produces<T>`) so the generated OpenAPI spec stays tight.
