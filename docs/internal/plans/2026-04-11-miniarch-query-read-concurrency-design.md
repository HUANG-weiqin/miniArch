# MiniArch Query Read Concurrency Design

Date: 2026-04-11

## Goal

Make query usage safe for concurrent read-only execution across multiple threads when the `World` is not being mutated during the query phase.

The target behavior is:

- multiple threads can enumerate the same `Query` concurrently
- multiple threads can enumerate different `Query` instances from the same `World` concurrently
- the runtime must not add locks to the query read path
- query performance should stay close to the current cached implementation

This design explicitly does not support concurrent query reads while another thread is performing `Create`, `Add`, `Set`, `Remove`, or `Destroy`.

## Current Problem

The current `ChunkEnumerator` is mostly local-state based, but `Query` itself is not safe for concurrent readers:

- `Query` stores `_matchingArchetypes` in a shared mutable `List<Archetype>`
- `Query.RefreshIfNeeded()` clears and repopulates that shared list in place
- concurrent first-time refreshes on the same `Query` can race on `_matchingArchetypes`, `_cachedWorldGeneration`, and `RefreshCount`
- `World.GetOrCreateQuery()` uses a mutable `Dictionary<QueryFilter, Query>` and is not safe if multiple threads try to materialize the same filter at the same time
- `Query` currently reads `World.Archetypes` through a live mutable dictionary value collection instead of an immutable read snapshot

Under the agreed usage model, the world is quiescent during reads, so we do not need a heavy synchronization model. We only need to ensure readers never observe partially refreshed shared query state.

## Recommended Approach

Use copy-on-write snapshots for query-visible state.

### Query cache publication

`World` should publish immutable snapshots for query-visible archetypes:

- maintain an `Archetype[]` snapshot for all archetypes
- rebuild and republish that array only when a new archetype is created
- readers only iterate the published array, never the live dictionary view

This keeps the read path on array traversal and avoids locks.

### Query match cache publication

`Query` should stop mutating a shared `List<Archetype>` in place.

Instead:

- keep a published `Archetype[]` match snapshot
- on refresh, build a new local array from the current world archetype snapshot
- publish the new array only after it is fully populated
- read-side enumeration captures one snapshot reference at construction time

This gives concurrent readers a stable immutable view without copying per enumeration.

### Query instance caching

`World.GetOrCreateQuery()` should avoid mutating a shared dictionary in a way that is unsafe for concurrent query creation.

The simplest fit for the current constraints is also copy-on-write:

- keep a published `Dictionary<QueryFilter, Query>` reference
- lookups read the currently published dictionary
- on cache miss, create a shallow copy, add the new query, then publish the new dictionary reference

This keeps reads lock-free and acceptable because query creation is expected to be much colder than query enumeration.

## Why Not Other Approaches

### Why not lock around query refresh

That would solve correctness, but it puts explicit synchronization in the query path and directly conflicts with the stated goal.

### Why not use `ConcurrentDictionary`

It would simplify some cache creation cases, but the hot path we care about is query enumeration, not map mutation. A small copy-on-write map keeps semantics tighter and avoids introducing a heavier synchronization primitive into the design.

### Why not add a separate read snapshot API

That would be the cleanest explicit API boundary, but it expands the public model and forces callers into a new usage mode. The current goal can be met inside the existing API surface.

## Runtime Semantics

After the change:

- a `Query` enumeration sees a stable set of matching archetypes captured from one published query snapshot
- if the query cache refreshes concurrently on another thread for the same world generation, readers still see either the old fully-built snapshot or the new fully-built snapshot, never a half-written list
- if multiple threads materialize the same filter concurrently, duplicate `Query` allocation may still happen transiently, but the published cache remains valid and safe

The design does not guarantee stable results across concurrent world writes. That remains outside the supported contract.

## Testing Strategy

Add deterministic concurrency regression tests for:

- concurrent enumeration of the same `Query`
- concurrent enumeration of separate `Query` instances targeting the same world
- concurrent first access to a cached query from multiple threads
- query refresh remaining logically correct after archetype generation changes and then entering a read-only phase

The tests should focus on:

- no exceptions
- consistent chunk counts
- consistent matched archetype counts
- repeated runs to reduce false confidence from one-off scheduling luck

## Acceptance Criteria

The work is complete when:

- query read-only concurrent usage is safe under the “world quiescent during reads” contract
- the query path remains lock-free
- existing query behavior stays intact
- new concurrency regression tests pass reliably
- the full relevant test suite passes
