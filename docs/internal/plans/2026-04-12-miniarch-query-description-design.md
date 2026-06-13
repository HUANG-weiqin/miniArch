# MiniArch Query Description Design

Date: 2026-04-12

## Goal

Add a reusable `QueryDescription` API to `MiniArch.Core` so callers can build a query shape once and reuse it via `world.Query(in description)` without introducing extra GC on the steady-state query path.

## Scope

- Add `MiniArch.Core.QueryDescription`.
- Add `MiniArch.Core.World.Query(in QueryDescription description)`.
- Keep the implementation inside `MiniArch.Core` only for this iteration.
- Reuse the existing `QueryFilter -> Query -> matching snapshot` pipeline.

## Constraints

- The description must be reusable across multiple `World` instances.
- The implementation must not add extra GC to warmed query usage.
- Existing query builder, query cache, and chunk traversal behavior must remain intact.
- The change should be minimal and avoid broad API reshaping.

## Options

### Option A: `QueryDescription` stores `Type`

Shape:

- `new QueryDescription().With<T>().Without<T>().WithAny<T>().Or<T>()`
- `world.Query(in description)` translates `Type` arrays into world-local `ComponentType` arrays and then builds a `QueryFilter`.

Pros:

- Safe to reuse across worlds.
- Reuses current world query cache unchanged.
- Minimal change surface.

Cons:

- Each new description instance still allocates while being built.
- Translation from `Type` to `ComponentType` still has a fixed cold cost.

### Option B: `QueryDescription` stores `ComponentType`

Pros:

- Cheapest translation into `QueryFilter`.

Cons:

- Not world-agnostic.
- Unsafe for cross-world reuse.
- Violates the persistence goal.

### Option C: `QueryDescription` precompiles directly to `QueryFilter`

Pros:

- `World.Query(...)` stays thin.

Cons:

- `QueryFilter` is world-bound through `ComponentType`.
- Would require broad refactoring for cross-world reuse.
- More invasive than needed.

## Decision

Choose Option A.

`QueryDescription` will be a value-semantic, immutable description that stores three sorted, deduplicated `Type[]` sets:

- `Required`
- `Excluded`
- `Any`

This keeps the description independent from any single `World` and allows `World.Query(in QueryDescription description)` to translate it into the existing `QueryFilter` model.

## API Shape

Core API for this iteration:

```csharp
var description = new QueryDescription()
    .With<Position>()
    .Without<Velocity>()
    .WithAny<TagA>()
    .Or<TagB>();

foreach (var entity in world.Query(in description))
{
}
```

Notes:

- `WithAny<T>()` is the preferred public name.
- `Or<T>()` remains an alias of `WithAny<T>()` to match the existing builder semantics.
- Empty descriptions keep the same meaning as `QueryFilter.Empty`: match all archetypes.

## Implementation Approach

### `QueryDescription`

- New file: `src/MiniArch/Core/QueryDescription.cs`
- Public readonly struct.
- Internally stores three `QueryDescriptionComponentSet` values backed by sorted `Type[]` arrays.
- Chain methods return new descriptions instead of mutating existing state.
- Equality and hashing are value-based.

### `World.Query(in QueryDescription description)`

- New overload in `src/MiniArch/Core/World.cs`.
- Translate `Type` arrays into world-local `ComponentType` arrays through the current component registry.
- Build a `QueryFilter` from translated component sets.
- Delegate to `GetOrCreateQuery(filter)`.

### Existing query pipeline

No changes planned for:

- `Query.cs`
- `QueryBuilder.cs`
- query snapshot invalidation
- chunk traversal and enumeration behavior

## GC Model

- Description construction may allocate because it is intended to be compiled once and reused.
- Steady-state repeated `world.Query(in sameDescription)` must not introduce new allocations beyond current cache behavior.
- Warmed `foreach` over the returned `Query` must keep the existing no-extra-allocation behavior.

## Testing

Add tests for:

- description required/excluded/any semantics
- `WithAny<T>()` and `Or<T>()` equivalence
- description query equivalence with `World.Query<T>()`
- description query equivalence with chain builder `Build()`
- same description returning the cached `Query`
- same description reused across multiple worlds
- allocation smoke coverage for warmed repeated usage

## Acceptance

This design is complete when:

- `MiniArch.Core.QueryDescription` exists and is reusable.
- `World.Query(in QueryDescription description)` works.
- new and existing query tests pass.
- no extra GC regression is introduced on warmed repeated usage.
