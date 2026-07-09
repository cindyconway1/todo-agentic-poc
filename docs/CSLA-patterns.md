# CSLA Patterns

CSLA-specific coding patterns for this codebase, documented to prevent code drift and
silent data loss across future features. Layer-wide conventions (stereotypes, property
registration, data-portal attributes) live in `.claude/rules/csla.md`; this file covers
the sharper-edged patterns that were discovered the hard way and the reasoning behind
them.

## Managed Collections: Use MobileList\<T\>, Not List\<T\>

When defining a **CSLA managed property** that holds a collection (list, set, etc.), use
`Csla.Core.MobileList<T>` instead of plain `List<T>`. This is critical for data
integrity.

### Why This Matters

The CSLA local data portal uses `MobileFormatter` to clone object graphs during save
operations. During this clone:

- **`MobileList<T>`** (implements `IMobileObject`) → serialized and restored intact; the
  collection survives the round-trip into the data portal.
- **`List<T>`** → **silently dropped**. `MobileFormatter` only carries managed fields
  whose values are primitives (or other types it knows how to serialize) or implement
  `IMobileObject`. A plain `List<T>` is neither, so the clone that the `[Insert]` /
  `[Update]` method actually operates on arrives with the property **null/empty** — and
  no exception is ever thrown.

### The Failure Mode Is Silent

This is the dangerous part: everything compiles, business rules pass, `SaveAsync()`
returns success — but the collection the caller set is gone by the time the data-portal
method runs, so nothing is persisted for it. In this repo that bug would have meant
volunteer team tags never saving, and it was only caught because a unit test saved
through the **real local data portal** and asserted the join rows afterwards (see
`tests/ToDo.UnitTests/VolunteerEditTagOwnershipTests.cs`, which initially failed for
exactly this reason).

### The Pattern

From `src/ToDo.Business/VolunteerEdit.cs` — back the property with `MobileList<T>`, but
expose a read-only surface publicly:

```csharp
public static readonly PropertyInfo<MobileList<Guid>> TeamIdsProperty =
    RegisterProperty<MobileList<Guid>>(nameof(TeamIds));
public IReadOnlyList<Guid> TeamIds
{
    get => GetProperty(TeamIdsProperty) ?? [];
    set => SetProperty(TeamIdsProperty, new MobileList<Guid>(value.Distinct().ToList()));
}
```

Notes on the shape:

- The public type is `IReadOnlyList<Guid>`, so callers can't mutate the backing list in
  place and bypass `SetProperty` (which is what marks the object dirty and runs rules).
- The setter normalizes input (here: `Distinct()` collapses duplicates into a set) and
  always wraps it in a fresh `MobileList<Guid>`.
- The getter falls back to an empty list so the property is never null to consumers.

In `[Create]` and `[Fetch]`, initialize/load with `LoadProperty` and an explicit
`MobileList<T>` so the field is populated without triggering rules:

```csharp
[Create]
private void Create()
{
    Id = Guid.NewGuid();
    LoadProperty(TeamIdsProperty, new MobileList<Guid>());
    BusinessRules.CheckRules();
}
```

```csharp
// inside [Fetch]
LoadProperty(TeamIdsProperty, new MobileList<Guid>(teamIds));
```

### When a MobileList of IDs Is Enough (vs. a Child Collection)

A full CSLA child collection (`BusinessListBase<T, C>`) is the right tool when the child
rows carry their own editable fields, rules, or per-item dirty tracking. When the
"children" are pure join rows with no fields of their own — like `VolunteerTeams`
(volunteer ID + team ID and nothing else) — a `MobileList<Guid>` of IDs reconciled in the
data portal (add missing rows, remove absent ones) is simpler and sufficient. See
`VolunteerEdit.UpdateAsync` for the reconcile pattern.

### Rule of Thumb for Managed Field Types

Safe as a managed property type:

| Type | Survives MobileFormatter clone? |
| --- | --- |
| Primitives, `string`, `Guid`, `DateTime`, `decimal`, and their nullables | ✅ |
| `Csla.Core.MobileList<T>` (of serializable `T`) | ✅ |
| CSLA business objects / lists (`BusinessBase`, `BusinessListBase`, …) | ✅ (they implement `IMobileObject`) |
| `List<T>`, `T[]`, `Dictionary<K,V>`, `HashSet<T>`, arbitrary POCOs | ❌ **silently dropped** |

### Checklist for Any New Collection-Bearing Business Object

1. Back the managed property with `MobileList<T>`, never `List<T>` or an array.
2. Expose it publicly as `IReadOnlyList<T>` (or similar) and rewrap in the setter.
3. Use `LoadProperty(..., new MobileList<T>(...))` in `[Create]`/`[Fetch]`.
4. Add a unit test that goes through the **real local data portal** (`SaveAsync`) and
   asserts the collection's effects afterwards — an in-memory rules test won't catch a
   dropped field, because the drop only happens in the data-portal clone.
