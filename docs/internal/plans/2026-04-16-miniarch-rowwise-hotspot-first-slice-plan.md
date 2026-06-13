# MiniArch Row-wise Hotspot First Slice Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Close the largest internal query gap by making component row-wise execution hoist component index resolution and row validation out of the per-row hot path.

**Architecture:** Keep the public query API unchanged. Add a narrow internal fast path in `Chunk`/query execution so component column indices are resolved once per chunk, then reused for all rows in that chunk. Preserve the existing row-wise behavior as a fallback so the change stays low-risk and easy to verify.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet, PowerShell

---

### Task 1: Add a regression test for row-wise vs span equivalence

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing test**

- Add a focused test that exercises the standard component query world and asserts:
  - row-wise checksum remains identical before/after the refactor
  - component-span checksum remains identical
  - row-wise and span still observe the same total results for the same world/query input

**Step 2: Run the focused test to verify it fails or is incomplete**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~QueryTests" -v minimal
```

Expected:
- The new test should fail until the internal fast path exists, or at minimum identify the missing optimization path in the benchmark instrumentation.

**Step 3: Commit**

```bash
git add tests/MiniArch.Tests/Core/QueryTests.cs
git commit -m "test: lock row-wise query equivalence before optimization"
```

### Task 2: Hoist component index resolution out of the row loop

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch.Core/Chunk.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch.Core/Query.cs` (or the actual query execution path that performs row-wise iteration)

**Step 1: Add the minimal internal fast path**

- Introduce an internal helper that resolves `ComponentType` to column indices once per chunk.
- Reuse those resolved indices inside the row loop rather than calling `GetComponentIndex(...)` per row.
- Keep public APIs unchanged.

**Step 2: Preserve fallback behavior**

- Keep the current row-wise helper available for safety / clarity.
- Only route the hot loop through the pre-resolved indices in the optimized path.

**Step 3: Run the targeted test and benchmark slice**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~QueryTests" -v minimal
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --filter "*Components_Execute_Warmed*" -j short
```

Expected:
- Tests pass.
- The warmed component benchmark slice improves the row-wise path relative to the previous baseline.

**Step 4: Commit**

```bash
git add src/MiniArch.Core/Chunk.cs src/MiniArch.Core/Query.cs
git commit -m "perf: hoist row-wise component lookup out of hot loop"
```

### Task 3: Re-run profiling to confirm the gap narrowed

**Files:**
- Modify: none

**Step 1: Re-run profiling workload comparison**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/profile-query.ps1 -Workload component-row-wise -Scenario with-all -Temperature hot -DurationSeconds 5 -WarmupIterations 2 -StartupDelaySeconds 0
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/profile-query.ps1 -Workload component-span -Scenario with-all -Temperature hot -DurationSeconds 5 -WarmupIterations 2 -StartupDelaySeconds 0
```

**Step 2: Compare against baseline**

- Confirm component-row-wise is materially closer to component-span than before.
- If the gap is still large, use the results to choose the next narrow slice rather than broad refactoring.

**Step 3: Verify repository health**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/verify.ps1
```

Expected:
- Build and tests still pass.

### Task 4: Update knowledge if the slice changes the model

**Files:**
- Modify: `.knowledge/kb-profiling-workflow.md`
- Modify: `.knowledge/kb-test-workflow.md`

**Step 1: Capture the new rule**

- Document the row-wise optimization decision and the new baseline numbers.

**Step 2: Keep terminology consistent**

- Use the same workload names and benchmark commands as the profiling plan.

**Step 3: Verify knowledge consistency**

Run:
```powershell
rg -n "component-row-wise|component-span|GetComponentIndex|profile-query" E:/godot/arch/miniArch/.knowledge
```
