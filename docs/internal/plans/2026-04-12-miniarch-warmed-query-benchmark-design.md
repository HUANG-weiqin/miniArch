# MiniArch Warmed Query Benchmark Design

Date: 2026-04-12

## Goal

Add a steady-state query benchmark path that measures query traversal on the final world shape instead of mixing builder cost, historical intermediate archetypes, and chunk traversal in one number.

## Context

The current complex query benchmark still grows the MiniArch world through repeated `Add` operations after the initial `Create`. That leaves intermediate archetypes behind and keeps query-construction overhead inside the measured region.

This is directionally useful, but it is not the cleanest signal for ranking read-path optimizations.

## Recommended Approach

Keep the existing query benchmarks, but improve the world shape and add warmed-query variants.

1. Build the complex query world directly into each final archetype by using `World.Create<T...>` with the full component set for that archetype.
2. Keep the existing benchmark methods as the mixed "build + execute" signal.
3. Add warmed-query benchmark methods that:
   - reuse deterministic final-world state
   - reuse a prebuilt MiniArch query object
   - warm the MiniArch query match cache during setup
   - reuse prebuilt Arch `QueryDescription` instances

This gives us two complementary signals:

- mixed benchmark: public API cost closer to current user code
- warmed benchmark: steady-state traversal cost

## Scope

In scope:

- direct final-world construction for the complex query benchmark factory
- warmed-query benchmark methods for the three existing scenarios
- tests that lock the final-world shape and warmed-query population contracts
- knowledge updates for the new benchmark interpretation

Out of scope:

- runtime query engine refactors
- copy-on-write cache redesign
- chunk span APIs
- structural-change benchmark changes

## Validation

Success means:

- `scripts/verify.ps1` passes
- complex query scenario tests still pass with tighter final-world expectations
- the query benchmark suite still runs
- at least one warmed-query benchmark result is measurably better than the mixed benchmark result for the same scenario, with no obvious regression in the existing query benchmarks

## Risks

- Direct-create world construction may accidentally change archetype populations if component sets drift.
- Warmed-query benchmarks can become misleading if setup accidentally leaks into measurement.
- BenchmarkDotNet variance may hide very small wins, so acceptance should focus on consistent direction rather than a single microsecond target.
