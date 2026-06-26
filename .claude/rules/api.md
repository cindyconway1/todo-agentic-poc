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

## Models and mapping

- Expose **POCO models** at the API boundary, not business objects.
- Map with **AutoMapper** using direction-specific profiles (separate request→domain and domain→response).

## Error model

- Return **422** for validation failures with the shape `{ id, message, errors[], warnings[] }`.
- **Sanitize SQL exceptions** before returning them — surface a reference ID, never raw SQL/exception detail.

## OpenAPI

- Return **typed DTOs** with **documented status codes** (declare every response type via `[ProducesResponseType]`/`Produces<T>`) so the generated OpenAPI spec stays tight.
