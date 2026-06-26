---
description: CSLA 10 business-object patterns for the Business layer
paths:
  - src/ToDo.Business/**
---

# CSLA 10 patterns

This layer uses CSLA 10. Follow CSLA's conventions rather than plain POCO/DTO patterns.

## Object stereotypes

Pick the right base class — don't write plain classes:
- `BusinessBase<T>` — editable root or child objects
- `BusinessListBase<T, C>` — editable collections
- `ReadOnlyBase<T>` / `ReadOnlyListBase<T, C>` — read-only objects and lists
- `CommandBase<T>` — operations that aren't CRUD on an object (e.g. a bulk action)

Prefer the project's **custom base classes** over the raw CSLA bases where they exist.

## Properties

Register every property with `RegisterProperty` (use the **lambda overload** for refactor-safe names), then access through the managed-field methods — never back with a plain field:
- Read/write inside the object: `GetProperty(...)` / `SetProperty(...)`
- Load without triggering rules/auth (inside data portal methods): `LoadProperty(...)`

Keep the conventional **region order** within a business object (property declarations, then business rules, then data access).

## Data access (data portal)

Decorate operations with the data-portal attributes — `[Create]`, `[Fetch]`, `[Insert]`, `[Update]`, `[DeleteSelf]`, `[Delete]` — and make them `async` where they hit the database. Put the EF Core access **inside** these operations, with dependencies (repositories, `DbContext`) supplied via `[Inject]`; do not resolve services manually or new them up.

- Follow the project's **multi-database naming** convention when more than one database/context is involved.
- Apply **defense-in-depth access checks** at the data portal (don't rely on the API layer alone for authorization).

## Creating and fetching objects

Never instantiate business objects with `new`. Inject `IDataPortal<T>` (or `IChildDataPortal<T>` for children) and call `CreateAsync` / `FetchAsync`. Register portals via CSLA's DI setup in `Program.cs` (`AddCsla`).

## Validation and authorization

- Add validation in the object's `AddBusinessRules()` override via `BusinessRules.AddRule(...)`; use the project's `UpwardSimplePropertyRule` for property rules.
- Add per-type authorization in `AddObjectAuthorizationRules()`; per-property auth via rules in `AddBusinessRules()`. Key authorization off the project's **activity constants**, not string literals.
- Don't scatter validation across the API layer — keep it in the business object.

## Layering

The Business layer owns domain logic and validation. EF Core / SQL access belongs in `ToDo.DataAccess` and should be reached from data portal methods via injected abstractions, not referenced directly from the API.