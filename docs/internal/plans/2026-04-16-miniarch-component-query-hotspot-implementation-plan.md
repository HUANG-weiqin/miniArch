# MiniArch Component Query Hotspot Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a component-query-specific observation workflow so MiniArch can profile, benchmark, and compare `component-consuming query` hotspots before changing runtime code.

**Architecture:** Extend the existing query profiling runner to execute `entity`, `component-row-wise`, and `component-span` workloads on the same benchmark world shape already used by BenchmarkDotNet and throughput. Keep runtime behavior unchanged in this iteration; only add observability and the minimal regression coverage needed to trust the measurements.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet, PowerShell

---

### Task 1: Add a failing test for profiling workload parsing

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs`

**Step 1: Write the failing test**

- Add a parsing-focused test that expects `QueryProfilingOptions.TryParse(...)` to accept a new workload argument with values:
  - `entity`
  - `component-row-wise`
  - `component-span`
- Assert invalid workload input is rejected.

**Step 2: Run the targeted test to verify it fails**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~ThroughputRunnerTests|FullyQualifiedName~QueryProfiling" -v minimal
```

Expected:
- The new parsing test fails because the profiling runner does not yet support a workload argument.

### Task 2: Extend QueryProfilingRunner to support component workloads

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs`

**Step 1: Add workload model**

- Introduce a profiling workload enum or equivalent internal model:
  - `Entity`
  - `ComponentRowWise`
  - `ComponentSpan`
- Extend `QueryProfilingOptions` parsing and CLI help text accordingly.

**Step 2: Route execution through workload-specific helpers**

- Keep the existing benchmark world construction.
- Add explicit execute helpers for:
  - entity checksum
  - component row-wise checksum
  - component span checksum
- Reuse the world state's cached `PositionType` and `VelocityType` for component workloads.

**Step 3: Keep hot/cold behavior unchanged**

- `hot` still reuses the same query instance.
- `cold` still clones a fresh query.
- Only the execute path changes per workload.

**Step 4: Run targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~ThroughputRunnerTests|FullyQualifiedName~QueryProfiling" -v minimal
```

Expected:
- Parsing and profiling-runner tests pass.

### Task 3: Add regression tests for the new profiling workload surface

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Add CLI-level regression coverage**

- Add a test that validates the profiling command line accepts:
  - `--workload entity`
  - `--workload component-row-wise`
  - `--workload component-span`

**Step 2: Add workload consistency coverage**

- Add a small test that builds the standard complex query world and verifies:
  - component row-wise checksum is stable
  - component span checksum is stable
  - both paths observe the same total checksum for the same query/world input

**Step 3: Run the focused tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~QueryTests|FullyQualifiedName~ThroughputRunnerTests" -v minimal
```

Expected:
- Focused tests pass and guard the new observation surface.

### Task 4: Update the profiling PowerShell entrypoint

**Files:**
- Modify: `E:/godot/arch/miniArch/scripts/profile-query.ps1`

**Step 1: Add workload parameter**

- Introduce a `-Workload` parameter with default `entity`.
- Pass it through to the benchmark runner as `--workload`.

**Step 2: Keep backward compatibility**

- Existing scenario, temperature, duration, warmup, and startup-delay parameters should continue to work.

**Step 3: Verify the script wiring**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/profile-query.ps1 -Workload component-span -Scenario with-all -Temperature hot -DurationSeconds 1 -WarmupIterations 1 -StartupDelaySeconds 0
```

Expected:
- The command starts successfully and prints the profiling workload metadata plus a completed iteration count.

### Task 5: Document the component-query profiling workflow

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-profiling-workflow.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Update profiling knowledge**

- Document the new workload dimension and the recommended commands for:
  - `entity`
  - `component-row-wise`
  - `component-span`

**Step 2: Update test/benchmark knowledge**

- Add the new profiling entry to the query validation flow.
- Clarify that component-query optimization should use:
  - BenchmarkDotNet for row-wise vs span A/B
  - throughput for fixed-duration ops/s
  - profiling for hotspot attribution

**Step 3: Verify knowledge consistency**

Run:
```powershell
rg -n "component-span|component-row-wise|profile-query" E:/godot/arch/miniArch/.knowledge
```

Expected:
- The updated workflow is present in the relevant knowledge pages and terminology is consistent.

### Task 6: Capture the first hotspot baseline

**Files:**
- Modify: none

**Step 1: Run the warmed component benchmark slice**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --filter "*Components_Execute_Warmed*" -j short
```

Expected:
- The warmed row-wise and span benchmark slice completes successfully.

**Step 2: Run the throughput slice**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/throughput.ps1 -Workload query-with-all-component-span -DurationSeconds 3 -RepeatCount 3
```

Expected:
- A repeatable `ops/s` comparison is produced for the component span workload.

**Step 3: Run the profiling slice**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/profile-query.ps1 -Workload component-span -Scenario with-all -Temperature hot -DurationSeconds 5 -WarmupIterations 2 -StartupDelaySeconds 0
```

Expected:
- The profiling runner completes and can be used with `dotnet-trace` or another sampler to inspect hotspot distribution.

### Task 7: Use the baseline to choose the first runtime optimization slice

**Files:**
- Modify: none

**Step 1: Compare row-wise vs span**

- If `component-span` is already much better than row-wise but still behind Arch, prioritize `GetComponentSpan<T>()` and per-chunk fixed cost analysis.
- If `component-span` and row-wise are both poor, investigate broader query/chunk traversal overhead first.

**Step 2: Inspect top samples**

- Use the profiler output to rank these likely first targets:
  - `Chunk.GetComponentSpan<T>()`
  - `Chunk.GetComponentSpanAt<T>()`
  - `Chunk.GetComponentIndex(...)`
  - outer query/chunk traversal wrappers

**Step 3: Write the next optimization design**

- Use the hotspot evidence to create the next design doc with a narrow implementation scope.

### Task 8: Final verification

**Files:**
- Modify: none

**Step 1: Run repository verification**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/verify.ps1
```

Expected:
- Build and tests pass after the observation-layer changes.

**Step 2: Commit**

```bash
git add docs/plans/2026-04-16-miniarch-component-query-hotspot-design.md docs/plans/2026-04-16-miniarch-component-query-hotspot-implementation-plan.md benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs scripts/profile-query.ps1 tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs tests/MiniArch.Tests/Core/QueryTests.cs .knowledge/kb-profiling-workflow.md .knowledge/kb-test-workflow.md
git commit -m "plan: add component query hotspot observation workflow"
```
