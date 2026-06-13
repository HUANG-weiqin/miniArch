# MiniArch Warmed Query Benchmark Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a final-world warmed-query benchmark path so complex query measurements separate steady-state traversal from builder and historical-archetype overhead.

**Architecture:** Update `BenchmarkWorldFactory` to construct final query archetypes directly with `Create<T...>`. Extend query benchmark state to hold prebuilt query shapes, and add warmed benchmark methods that reuse these objects after setup. Lock the new benchmark contract with tests before changing the benchmark implementation.

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet.

---

### Task 1: Add failing tests for final-world benchmark shape

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ComplexQueryBenchmarkScenarioTests.cs`

**Step 1: Write the failing tests**

- Add a test asserting the MiniArch complex query benchmark world leaves no empty archetypes.
- Add a test asserting the MiniArch benchmark state exposes warmed queries whose first execution has already populated the match cache.

**Step 2: Run the targeted tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~ComplexQueryBenchmarkScenarioTests`

Expected: fail because the current world factory still leaves historical empty archetypes and the warmed-query state does not exist yet.

### Task 2: Build the final-world benchmark shape directly

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/BenchmarkWorldFactory.cs`

**Step 1: Replace incremental `Add` world construction with direct final-archetype creation**

- For each benchmark group, call `World.Create<T...>` with the full final component set.
- Keep archetype populations and query hit ratios unchanged.

**Step 2: Extend benchmark state for warmed queries**

- Add cached MiniArch queries for the three scenarios.
- Warm them during state construction.
- Add cached Arch `QueryDescription` instances for the same scenarios.

### Task 3: Add warmed benchmark methods

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs`

**Step 1: Keep the existing mixed methods intact**

- Reuse the improved final-world factory.

**Step 2: Add warmed variants**

- Add warmed benchmark methods for:
  - `WithAll`
  - `WithAll + Without`
  - `WithAll + Any`
- Use prebuilt query state so the measured region focuses on execution.

### Task 4: Verify benchmark behavior

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Run targeted tests**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~ComplexQueryBenchmarkScenarioTests`

Expected: pass.

**Step 2: Run full verification**

Run: `powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/verify.ps1`

Expected: pass.

**Step 3: Run query benchmarks**

Run: `powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/benchmark.ps1 -Filter "*QueryBenchmarks*"`

Expected: query benchmarks complete, existing methods do not obviously regress, and warmed variants provide a cleaner lower steady-state signal.

### Task 5: Write back benchmark guidance

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Record the warmed-query benchmark guidance**

- Explain when to use mixed query benchmarks versus warmed query benchmarks.
- Note that final-world construction is intended to avoid historical empty-archetype pollution.
