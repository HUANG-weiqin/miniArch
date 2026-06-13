# MiniArch Query Sampling Profile Design

Date: 2026-04-12

## Goal

Add a reproducible sampling-profile entry point for MiniArch query workloads so we can identify the slowest functions in the query path without injecting timers or counters into `MiniArch.Core`.

The profiling flow should:

- reuse the existing deterministic complex query benchmark world
- keep runtime hot paths unchanged
- support both hot-query traversal sampling and cold-query matching sampling
- expose a stable process window that external samplers can attach to
- keep the invocation simple enough to rerun during optimization work

## Recommended Approach

Use an independent profiling harness inside `MiniArch.Benchmarks`, not BenchmarkDotNet instrumentation inside `MiniArch.Core`.

The harness should:

- build a fixed world through `BenchmarkWorldFactory`
- build the target query shape through the existing public query API
- optionally clone a fresh `MiniArch.Core.Query` instance per iteration in cold mode so `BuildMatchingArchetypes` and `Matches` stay visible in samples
- loop for a configurable time window
- print the process id, selected workload, and final loop statistics

This keeps the runtime clean while still giving external samplers a stable, long-running target.

## Workload Modes

Support 3 logical query scenarios:

- `with-all`
- `with-all-without`
- `with-all-any`

Support 2 execution temperatures:

- `hot`
  - warm the query cache first
  - repeatedly execute the same query to expose traversal hotspots
- `cold`
  - create a fresh query object per iteration against the same world
  - force `RefreshIfNeeded -> BuildMatchingArchetypes -> Matches` back into the sample set

Cold mode should avoid mutating the world. The preferred implementation is to create a fresh `Query` instance through reflection from the existing cached query's internal filter.

## CLI Contract

The benchmark executable should accept a dedicated profiling verb, for example:

`dotnet run --project benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release -- profile-query --scenario with-all --temperature cold --entity-count 100000 --duration 15`

The profiling verb should support:

- `--scenario`
- `--temperature`
- `--entity-count`
- `--duration`
- `--warmup`
- `--startup-delay`

Defaults should favor the common hotspot workflow:

- scenario: `with-all`
- temperature: `cold`
- entity-count: `100000`
- duration: `15`
- warmup: `3`
- startup-delay: `3`

## Verification Strategy

We do not need to prove absolute benchmark numbers here. We only need to prove the profiling harness works and is stable enough for sampling.

Validation should cover:

- argument parsing chooses expected defaults and accepts explicit overrides
- the runner completes inside a bounded duration and produces non-zero iterations
- the hot and cold paths both execute the intended query workloads
- the CLI wrapper script launches the profiling verb through the benchmark project

Manual acceptance:

- run the profiling script in Release
- attach PerfView or Visual Studio CPU Usage during the startup delay
- confirm the trace shows MiniArch query call stacks

## Risks

- If the profiling loop is too short, sample counts will be too noisy to interpret.
- If cold mode mutates the world to invalidate caches, sampler output will be polluted by structural-change costs.
- If the CLI path is mixed into BenchmarkDotNet startup flow, the sample window will include harness overhead instead of query work.

## Acceptance Criteria

This design is complete enough when:

- a dedicated profiling command exists
- it can run MiniArch complex query workloads for a fixed time window
- hot and cold query modes are both available without modifying `MiniArch.Core`
- the repository contains a documented rerun path for external CPU sampling
