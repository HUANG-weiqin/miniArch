# MiniArch Entity Query Hotpath Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce warmed entity-only query overhead by replacing the hot-path dual generation checks with a single world query generation while keeping query semantics and public API unchanged.

**Architecture:** Add one world-side invalidation generation that advances whenever query-visible archetype or chunk layout changes occur, then make `Query` snapshots key off that single generation. Keep the existing snapshot build and CAS publication pattern so concurrent readers still observe immutable cached arrays.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet, PowerShell

---

### Task 1: Add a failing regression test for unified query invalidation generation

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing test**

- Add a reflection-based test that expects `World` to expose one internal unified query generation member.
- Assert the generation value increases after creating a new matching archetype and after a later matching entity changes query-visible layout.
- Assert a warmed query still keeps `RefreshCount` stable when the world has not changed.

**Step 2: Run the targeted test to verify it fails**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~QueryTests -v minimal
```

Expected: the new test fails because the unified generation member does not exist yet.

### Task 2: Implement the unified query generation path

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Query.cs`

**Step 1: Write the minimal implementation**

- Add a world-side unified query generation field and internal accessor.
- Advance it everywhere the old query invalidation inputs already advanced.
- Change `Query.MatchingSnapshot` and hot-path checks to use one generation instead of `ArchetypeGeneration` + `QueryLayoutGeneration`.
- Keep refresh publication and `RefreshCount` semantics unchanged.

**Step 2: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~QueryTests -v minimal
```

Expected: `QueryTests` pass, including the new generation regression.

### Task 3: Verify no broader query regressions

**Files:**
- Modify: none

**Step 1: Run focused query-related tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~QueryTests|FullyQualifiedName~ComplexQueryBenchmarkScenarioTests" -v minimal
```

Expected: all focused query/benchmark-scenario tests pass.

### Task 4: Measure the warmed benchmark before/after

**Files:**
- Modify: none

**Step 1: Run the focused warmed benchmark**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --filter "*MiniArch_WithAll_Execute_Warmed*" -j short
```

Expected: the benchmark completes successfully and shows a clear improvement versus the saved baseline for the warmed MiniArch entity-only query path.

**Step 2: If the result is noisy, compare the same filter twice**

- Re-run the same short filter once more to confirm the direction is stable.
- Only expand scope if the result is flat and tests confirm the hot path is otherwise unchanged.
