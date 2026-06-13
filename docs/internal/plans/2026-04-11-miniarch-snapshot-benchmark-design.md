# MiniArch Snapshot Benchmark Design

Date: 2026-04-11

## Goal

Add a dedicated `BenchmarkDotNet` benchmark that measures first-version snapshot save/load cost for `MiniArch`.

## Scope

This benchmark should measure:

- `WorldSnapshot.Save`
- `WorldSnapshot.Load`
- snapshot byte size

It should use three fixed world sizes:

- 100 entities
- 500 entities
- 1000 entities

Each entity should carry exactly 10 unmanaged components so the benchmark reflects the intended snapshot workload rather than sparse toy worlds.

## Why A Dedicated Benchmark

The repository already separates query and structural-change benchmarks. Snapshot persistence is a distinct cost center:

- it includes serialization work
- it includes stream allocation / payload writing
- it includes world reconstruction on load
- it is not comparable to `Create / Add / Set / Remove / Destroy` hot paths

So it should live in its own benchmark type rather than being mixed into `StructuralChangeBenchmarks`.

## Recommended Approach

### Approach A: Dedicated SnapshotBenchmarks

Add a new benchmark class:

- `SnapshotBenchmarks`

with:

- `[Params(100, 500, 1000)]` for entity count
- a fixed benchmark world shape containing 10 unmanaged components per entity
- one benchmark for save
- one benchmark for load
- one additional exported metric for snapshot size

This is the recommended approach because:

- results stay focused on snapshot cost
- setup cost can be isolated into `IterationSetup`
- output remains easy to read

### Approach B: Add Snapshot Methods To StructuralChangeBenchmarks

This would reuse an existing file, but it would mix unrelated benchmark semantics and make the output table harder to read. Not recommended.

### Approach C: Compare Against JSON Or Another Serializer

This could be useful later, but it adds another design problem:

- what baseline is fair
- whether that baseline is tuned
- whether the benchmark is about ECS layout or serializer choice

Not recommended for the first pass.

## Benchmark Design

### World Shape

Use a deterministic world builder that:

- creates `N` entities
- attaches exactly 10 unmanaged components to each entity
- uses stable values derived from entity index

The benchmark should not depend on runtime randomness.

### Components

Reuse existing unmanaged benchmark structs where sensible and add the minimum extra unmanaged structs needed to reach 10 total components.

The components should remain simple:

- integers
- small fixed tuples
- enums if useful

No reference-type fields.

### Timing Model

The benchmark should exclude world construction from timed methods:

- `Save` benchmark uses a prebuilt world from setup
- `Load` benchmark uses precomputed snapshot bytes from setup

This keeps the measurement aligned with the stated goal.

### Size Metric

Snapshot size should be exported as a separate benchmark column based on the exact generated payload length in bytes.

This is preferable to logging because:

- it stays attached to the benchmark row
- it is visible in markdown exports
- it does not distort timing

## Expected Files

- `benchmarks/MiniArch.Benchmarks/SnapshotBenchmarks.cs`
- `benchmarks/MiniArch.Benchmarks/BenchmarkComponents.cs`
- `benchmarks/MiniArch.Benchmarks/BenchmarkWorldFactory.cs`
- optional update to `benchmarks/MiniArch.Benchmarks/MiniArchBenchmarkConfig.cs` only if needed for custom columns

## Verification

The work is complete when:

- the benchmark project builds
- `SnapshotBenchmarks` runs through BenchmarkDotNet
- output includes save time, load time, allocations, and snapshot size
- the benchmark covers 100 / 500 / 1000 entities with 10 unmanaged components each
