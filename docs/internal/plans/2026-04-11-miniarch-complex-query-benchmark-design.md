# MiniArch Complex Query Benchmark Design

Date: 2026-04-11

## Goal

Add a BenchmarkDotNet workload that compares MiniArch and Arch on realistic, more complex query traversal scenarios instead of only measuring query-builder creation.

The benchmark should:

- use multiple entity-count tiers
- guarantee at least 8 components per entity
- include multiple archetype layouts in the same world
- compare the same logical query workloads between MiniArch and Arch
- produce a short report that summarizes the performance picture

## Workload Model

The benchmark world should not be a single homogeneous archetype.

Instead, it should be a deterministic mix of several archetypes built from a shared component pool:

- a baseline archetype with 8 shared components
- one archetype that adds extra optional components and still matches most queries
- one archetype that misses one required component and should be filtered out
- one archetype that contains excluded components and should be filtered out by `Without`
- one archetype that only matches the `Any/Or` branch

This layout lets the benchmark exercise both:

- archetype filtering cost
- chunk traversal cost on matching archetypes

Every entity in the benchmark world must have at least 8 components so the query work happens in a denser, more realistic ECS layout than the existing `Position + Velocity` case.

## Entity Tiers

Default entity-count tiers:

- `10_000`
- `50_000`
- `100_000`

The per-tier distribution should stay deterministic and symmetric between MiniArch and Arch.

## Query Matrix

The benchmark should measure actual query execution, not only query description creation.

Use 3 representative query scenarios:

1. `WithAll`
   - requires several shared components
   - should hit the majority of the intended matching archetypes
2. `WithAll + Without`
   - requires several shared components
   - excludes a component present in one large archetype branch
3. `WithAll + Any/Or`
   - requires several shared components
   - includes an `Any` set that is only present in some archetypes

These three cases are enough to show:

- plain matching throughput
- exclusion filtering impact
- disjunction filtering impact

without turning the result set into an unreadable matrix.

## Measurement Rules

The measured region should cover query creation/build and full execution over the matching chunks/entities for each engine.

Rules:

- setup and world construction stay outside the measured region
- MiniArch and Arch receive the same archetype mix and same entity distribution
- the benchmark should consume the query results so the runtime cannot trivially optimize the loop away
- output interpretation must consider both `Mean` and allocation data

The benchmark is for hot-path comparison, not correctness proof. Correctness is enforced separately by unit tests on the scenario factory.

## Architecture

Implementation should stay close to the existing benchmark structure:

- extend `BenchmarkComponents.cs` with enough components to build the dense layouts
- extend `BenchmarkWorldFactory.cs` with deterministic world builders for the complex query scenarios
- replace or extend `QueryBenchmarks.cs` so it measures query execution across the new matrix
- keep `scripts/benchmark.ps1` as the main entry point
- add a small report file under `docs/` that summarizes the benchmark results in Chinese

## Risks

- If the benchmark world uses different archetype distributions between MiniArch and Arch, the comparison becomes meaningless.
- If setup leaks into the measured region, the reported query cost is distorted.
- If the query result is not consumed, the JIT may reduce the benchmark to an unrealistically small loop.
- If every archetype still looks too similar, the benchmark will not expose filtering cost in a meaningful way.

## Acceptance Criteria

This design is good enough when:

- the benchmark is runnable from the existing benchmark entry point
- each benchmark world uses at least 8 components per entity
- entity-count tiers cover more than one scale
- the benchmark compares MiniArch and Arch on the same complex query scenarios
- a short Chinese report summarizing the result is written into the repository
