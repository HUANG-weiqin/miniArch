# MiniArch Mixed Structural-Change Benchmark Design

Date: 2026-04-11

## Goal

Define a BenchmarkDotNet workload that compares MiniArch and Arch on the same mixed structural-change script so we can see both:

- time cost
- allocation / space cost

The benchmark should focus on the hot structural-change path, not on correctness validation.

## Workload Model

The default workload is a fixed-seed random mix of the five structural operations:

- `Create`
- `Add`
- `Set`
- `Remove`
- `Destroy`

Default mix:

- `20%` `Create`
- `20%` `Add`
- `20%` `Set`
- `20%` `Remove`
- `20%` `Destroy`

Rules:

- generate one deterministic operation script per benchmark case
- run MiniArch and Arch against the same script
- keep setup, world construction, and seed generation outside the measured region
- measure the steady-state hot path only

## Measurement Rules

The benchmark should report:

- `Mean`
- allocated bytes
- GC activity if BenchmarkDotNet exposes it for the run

Interpretation rules:

- `Mean` answers how fast the mixed structural-change path is
- allocated bytes answer how much transient memory the path burns
- the comparison only makes sense if both implementations receive identical entity layouts and identical operation order

## Architecture

The benchmark harness should treat both engines symmetrically:

- build comparable worlds first
- pre-populate the same starting population
- use the same component set and entity index pattern
- apply the same operation script in the same order
- keep reporting separate from setup so the measured work stays clean

The benchmark is intentionally not a correctness harness. Any functional mismatch should be caught by unit tests before benchmark results are trusted.

## Risks

- Randomness without a fixed seed will make the numbers non-reproducible.
- Mixing setup into the measurement region will hide the real structural-change cost.
- Comparing different entity layouts will make Arch and MiniArch numbers meaningless.
- Averaging only time and ignoring allocation can hide a regression in memory churn.

## Acceptance Criteria

This design is good enough when:

- the benchmark uses a deterministic 20/20/20/20/20 mixed structural-change script by default
- the same script is applied to both MiniArch and Arch
- setup is excluded from the measured region
- the output includes both time and allocation signals
- the results are directly comparable between the two implementations
