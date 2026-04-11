# MiniArch Query Filtering and Performance Design

Date: 2026-04-11

## Goal

Extend MiniArch’s ECS core with chainable query filters and a benchmark-driven optimization loop.

The new query model should keep the current `Query<T>()` compatibility entry point, but the primary API should become chain-based:

- `With<T>()`
- `Without<T>()`
- `Any<T>()`
- `Or<T>()`

The implementation should then be measured against Arch using BenchmarkDotNet, with the goal of keeping MiniArch within 2x of Arch on average for the targeted operations while avoiding avoidable GC.

## What Changes

### Query API

The current generic `World.Query<T1, ...>()` entry points remain available for compatibility, but they become thin wrappers around a new chainable query builder.

The chainable API should express:

- required components via `With<T>()`
- excluded components via `Without<T>()`
- disjunctions via `Any<T>()` and `Or<T>()`

Each method only needs a single type parameter. Users build wider queries by chaining repeated calls.

Recommended semantics:

- `With<T>()` means the archetype must contain `T`
- `Without<T>()` means the archetype must not contain `T`
- `Any<T>()` means the archetype must contain at least one component from the collected `Any` set
- `Or<T>()` is an alias for `Any<T>()`

If no `Any<T>()` calls are present, the any-clause is ignored.

### Internal Query Model

The query model should be split into two layers:

1. a lightweight builder used to compose filters without per-call heap churn
2. a cached runtime query object that stores the normalized filter state and matching archetypes

The runtime query should continue to:

- filter archetypes first
- iterate matching chunks second
- cache matches until the world archetype generation changes

The new filter model must remain compatible with the current archetype/signature storage model.

### Performance Direction

The performance work should prioritize:

- eliminating avoidable allocations in query creation and iteration
- avoiding LINQ, closures, boxing, and iterator adapter churn in hot paths
- keeping the structural-change path stable and predictable
- measuring reality instead of assuming improvements

The benchmark target is not raw absolute speed alone. The acceptance bar is the average benchmark result against Arch for the same operation mix.

## Architecture

The ECS runtime still follows the same core chain:

- `World` owns component registration, archetype lookup, entity lifecycle, and query caching
- `Signature` still identifies archetypes by component set
- `Archetype` still owns chunks and transition edges
- `Chunk` still stores dense component columns
- `Query` still resolves matching archetypes before chunk traversal

The key change is that query construction becomes a chainable filter description instead of direct generic overloads. That lets us extend the filter language without exploding overload count, while keeping the existing runtime data flow intact.

For performance, the benchmark project will compare MiniArch and Arch on the same operations:

- query creation
- add
- set
- remove
- destroy

BenchmarkDotNet is the measurement layer. The benchmark project should isolate setup cost from steady-state operation cost so the mean values are meaningful.

## GC Strategy

The implementation should avoid avoidable GC in the hot path.

Practical rules:

- do not allocate a new query object for every repeated identical query description
- do not allocate per-iteration adapters while enumerating chunks
- do not use LINQ in runtime query matching or benchmark hot paths
- prefer value types, cached arrays, and reusable buffers where they are safe

The benchmark pass should treat allocation data as a first-class signal, not just an observation.

## Testing Strategy

Add tests for:

- chainable query filters matching the intended archetype sets
- compatibility of existing `Query<T>()` overloads
- `Without<T>()` excluding archetypes correctly
- `Any<T>()` and `Or<T>()` matching archetypes that contain at least one requested component
- repeated query creation reusing cached runtime query objects where the description is identical
- chunk traversal still visiting each matching chunk exactly once

The benchmark project should not replace unit tests. It should complement them.

## Risks

- The `Any` / `Or` semantics can get ambiguous if we try to support nested boolean expressions too early.
- The query builder can accidentally allocate if it leans on lists, captures, or iterator adapters.
- Benchmark results can be misleading if warmup, setup, or world construction leaks into the measured region.
- The Arch comparison can become meaningless if the operation mix is not normalized between the two implementations.

## Acceptance Criteria

The work is complete when:

- the chainable query API exists and the old `Query<T>()` entry points still work
- `With<T>()`, `Without<T>()`, `Any<T>()`, and `Or<T>()` are supported as single-parameter chainable filters
- query creation and iteration avoid avoidable allocations in the hot path
- a BenchmarkDotNet project exists and compares MiniArch against Arch
- the average benchmark result across the tracked operations is within the 2x target
- the query and structural-change tests still pass

