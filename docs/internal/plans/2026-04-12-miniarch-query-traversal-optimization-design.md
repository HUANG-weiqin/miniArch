# MiniArch Query Traversal Optimization Design

Date: 2026-04-12

## Goal

Based on the current query profiling results, choose one likely high-value optimization direction for MiniArch query performance and define how to validate it.

## Profiling Summary

Recent CPU sampling on `with-all + 100000 entities` shows:

- the dominant cost is in query execution traversal, not in query matching refresh
- `Query.BuildMatchingArchetypes` is a small fraction of total samples
- the hot path is the nested chunk/entity loop that ultimately reads entity rows

This means the first optimization iteration should not target `Matches` or `BuildMatchingArchetypes`.

## Options

### Option A: Span-first traversal path

Idea:

- keep current query filtering logic
- optimize the steady-state read path by iterating chunk entity spans directly
- avoid per-row accessor overhead such as repeated `GetEntity(row)` calls and their bounds checks / call indirection

Possible shape:

- keep `Chunk.GetEntities()` as the primitive span exposure
- add or prefer query-side APIs that consume `ReadOnlySpan<Entity>` or typed component spans
- ensure hot query benchmarks use the span path for MiniArch, because that is the path we actually want to optimize

Pros:

- directly targets the current hotspot
- minimal risk to query correctness semantics
- aligns with ECS dense-storage expectations

Cons:

- public API decisions matter; adding too many span-based variants can fragment the surface
- if the real cost later turns out to be typed component access rather than entity access, this is only the first step

### Option B: Flatten matched chunks during refresh

Idea:

- when query refresh happens, build a flat `Chunk[]` snapshot instead of only `Archetype[]`
- traversal then iterates a single chunk array, avoiding archetype/chunk nested switching

Pros:

- simple mental model
- refresh cost is already small, so paying a bit more there is acceptable for reused queries

Cons:

- likely smaller upside than Option A when row count dominates
- increases refresh-time allocation unless carefully pooled

### Option C: Typed query specialization

Idea:

- move from generic chunk traversal to typed query execution helpers such as `Query<T1, T2>.ForEach(...)`
- expose component spans directly and bypass repeated per-row generic accessor work

Pros:

- largest long-term ceiling
- best fit for ECS-style tight loops

Cons:

- most invasive API and implementation change
- much larger validation surface
- not suitable as the first optimization step without a smaller proof first

## Recommendation

Recommend Option A first: treat query as a span-first traversal problem.

The reason is simple:

- profiling says traversal is the bottleneck
- current matching cost is small
- Option A attacks the hotspot with the smallest architecture disturbance

In practical terms, the first implementation slice should be:

1. make sure the hot path can traverse `ReadOnlySpan<Entity>` without per-row `GetEntity(row)`
2. if the result is material, extend the same idea to component columns with typed spans
3. only after that, reconsider chunk flattening or typed query specialization

## Validation

Validation should use both benchmark and sampling:

- benchmark:
  - compare `MiniArch complex query ... warmed` before and after
  - focus on warmed cases first, because they isolate steady-state traversal
- profiling:
  - rerun `scripts/profile-query.ps1 -Temperature hot`
  - confirm the top samples move away from the traversal wrapper and per-row access path
- guardrail:
  - keep `cold` profiling around to ensure we did not accidentally regress matching while optimizing traversal

## Acceptance

This design is useful when:

- it gives a concrete first optimization target
- it explains why matching is not the first place to spend effort
- it defines a verification loop that can prove whether the optimization actually helped
