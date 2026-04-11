# MiniArch Complex Query Benchmark Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a runnable complex query benchmark that compares MiniArch and Arch across multiple entity-count tiers and dense multi-component archetype layouts, then write a short Chinese result summary.

**Architecture:** Build deterministic benchmark worlds with several dense archetypes from a shared component pool. Lock the scenario contract with unit tests first, then extend the benchmark factory and query benchmarks to measure actual query execution for `WithAll`, `WithAll + Without`, and `WithAll + Any/Or` style workloads. Finish by running the benchmark and recording the outcome in a short report.

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet, Arch.

---

### Task 1: Lock the complex query scenario contract with tests

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing tests**

- Add a test that the complex benchmark world shape creates multiple archetypes instead of a single homogeneous one.
- Add a test that the intended benchmark query cases return different populations for `WithAll`, `WithAll + Without`, and `WithAll + Any/Or`.
- Add a test that the benchmark scenario uses at least 8 components per matching entity.

**Step 2: Run the targeted tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~QueryTests`

Expected: fail because the benchmark scenario helper does not exist yet.

**Step 3: Keep the tests focused on observable scenario behavior**

- Do not assert benchmark-specific implementation details such as exact internal arrays.
- Assert only the world shape and query-visible results needed by the benchmark contract.

---

### Task 2: Add the dense benchmark component set and world builders

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/BenchmarkComponents.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/BenchmarkWorldFactory.cs`

**Step 1: Extend the benchmark component pool**

- Add enough small value-type components to build 8+ component archetypes.
- Keep component definitions simple and deterministic.

**Step 2: Add deterministic complex-world builders**

- Add MiniArch and Arch builders that create the same archetype distribution and entity counts.
- Add helper state that exposes the built query descriptions or inputs needed by the benchmark methods.

**Step 3: Preserve comparability**

- Keep the same component combinations and population ratios across both engines.
- Keep world construction outside the benchmark measurement region.

---

### Task 3: Replace the lightweight query benchmark with complex query execution benchmarks

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs`

**Step 1: Add multi-tier benchmark parameters**

- Use `10_000`, `50_000`, and `100_000` as entity-count tiers.

**Step 2: Add the 3 query scenarios**

- `WithAll`
- `WithAll + Without`
- `WithAll + Any/Or`

**Step 3: Consume results in the measured methods**

- Traverse the matching query results and accumulate a simple checksum so the work is not optimized away.
- Keep MiniArch and Arch methods logically symmetric.

---

### Task 4: Verify the new benchmark end-to-end

**Files:**
- Modify: `E:/godot/arch/miniArch/scripts/benchmark.ps1` only if the existing entry point needs extra filter support

**Step 1: Run targeted tests**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~QueryTests`

Expected: pass.

**Step 2: Run a benchmark build check**

Run: `dotnet build E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --no-restore`

Expected: pass.

**Step 3: Run the query benchmark**

Run: `powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/benchmark.ps1 -Filter "*QueryBenchmarks*"`

Expected: BenchmarkDotNet produces Arch vs MiniArch results for the three complex query scenarios across the configured tiers.

---

### Task 5: Write the result summary and update knowledge

**Files:**
- Create: `E:/godot/arch/miniArch/docs/benchmarks/2026-04-11-complex-query-benchmark-report.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/INDEX.md` only if a new knowledge page is added

**Step 1: Write the benchmark report**

- Summarize the scenario shape.
- Summarize the main Arch vs MiniArch result by query class and scale.
- Keep the report concise and Chinese-first.

**Step 2: Update project knowledge**

- Record that query benchmarks now cover dense multi-archetype scenarios, not only query creation.
- Record where future agents should look when rerunning or interpreting the benchmark.

**Step 3: Re-read the updated knowledge**

Run: `Get-Content E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

Expected: the knowledge page accurately describes the new benchmark coverage.
