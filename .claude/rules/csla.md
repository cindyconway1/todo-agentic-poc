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

## Managed collections — use `MobileList<T>`, never `List<T>`

Back a collection-valued managed property with `Csla.Core.MobileList<T>`, **not** `List<T>` or an array. The local data portal clones the object graph with `MobileFormatter` during save, and it only carries managed fields that are primitives or implement `IMobileObject`. `MobileList<T>` survives the clone; a plain `List<T>`/`T[]`/`HashSet<T>`/`Dictionary<K,V>`/POCO is **silently dropped** — the `[Insert]`/`[Update]` method runs against a null/empty collection, no exception is thrown, and nothing persists. Primitives, `string`, `Guid`, `DateTime`, `decimal` (and nullables), and CSLA business objects/lists are also safe.

The shape (see `src/ToDo.Business/VolunteerEdit.cs`):

```csharp
public static readonly PropertyInfo<MobileList<Guid>> TeamIdsProperty =
    RegisterProperty<MobileList<Guid>>(nameof(TeamIds));
public IReadOnlyList<Guid> TeamIds
{
    get => GetProperty(TeamIdsProperty) ?? [];
    set => SetProperty(TeamIdsProperty, new MobileList<Guid>(value.Distinct().ToList()));
}
```

- Expose it publicly as `IReadOnlyList<T>` so callers can't mutate the backing list in place and bypass `SetProperty` (which marks dirty and runs rules); the setter normalizes and rewraps in a fresh `MobileList<T>`, the getter falls back to empty.
- In `[Create]`/`[Fetch]`, populate with `LoadProperty(TeamIdsProperty, new MobileList<T>(...))` so the field loads without triggering rules.
- A `MobileList<T>` of IDs reconciled in the data portal (add missing rows, remove absent ones — see `VolunteerEdit.UpdateAsync`) is enough for pure join rows with no fields of their own; reach for a full child collection (`BusinessListBase<T, C>`) only when the child rows carry their own editable fields, rules, or per-item dirty tracking.
- **Test it through the real local data portal:** add a unit test that `SaveAsync`s and asserts the collection's persisted effects. The drop happens only in the data-portal clone, so an in-memory rules test won't catch it (see `tests/ToDo.UnitTests/VolunteerEditTagOwnershipTests.cs`).

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