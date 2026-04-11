# MiniArch Query Filtering and Performance Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add chainable query filters, preserve compatibility with the current query entry points, and benchmark MiniArch against Arch while optimizing allocations and average performance.

**Architecture:** Keep the existing `World -> Archetype -> Chunk` runtime shape, but move query construction to a chainable filter model that normalizes into cached runtime queries. Add a separate BenchmarkDotNet project to compare MiniArch and Arch on the same operation set, then iterate on the hot path until allocations and average results meet the target.

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet, NuGet package `Arch`.

---

### Task 1: Add the query filter model

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Query.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/QueryIterators.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/QueryBuilder.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryFilterTests.cs`

**Step 1: Write the failing tests**

- Test that `World.Query().With<T>()` matches the same archetypes as the current generic query entry point.
- Test that `Without<T>()` excludes archetypes with the component.
- Test that `Any<T>()` and `Or<T>()` match archetypes containing at least one of the requested components.
- Test that repeated chain construction still returns the same cached runtime query for the same normalized filter description.

**Step 2: Run the tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryFilterTests`

Expected: fail because the chainable query API does not exist yet.

**Step 3: Write the minimal implementation**

- Add a chainable query builder entry point on `World`.
- Keep `Query<T1>()`, `Query<T1, T2>()`, and `Query<T1, T2, T3>()` as compatibility wrappers.
- Normalize filter state before it is cached.
- Make `Any<T>()` and `Or<T>()` share the same single-parameter disjunction path.

**Step 4: Run the tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryFilterTests`

Expected: pass.

**Step 5: Commit**

- Commit only the query API and tests for this task.

### Task 2: Remove avoidable allocations from the query hot path

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Query.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/QueryIterators.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing tests**

- Add a regression test that exercises repeated query creation and chunk enumeration without introducing extra heap churn in the normal path.
- Add a regression test that confirms chunk traversal still visits each matching chunk exactly once after the refactor.

**Step 2: Run the tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: fail if the current implementation still allocates or regresses the traversal behavior.

**Step 3: Write the minimal implementation**

- Remove LINQ and closure-based work from the query hot path.
- Keep query match refresh logic inside the runtime query object.
- Prefer reusable iteration state over adapter churn.
- Reuse cached query objects when the normalized description is identical.

**Step 4: Run the tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: pass.

**Step 5: Commit**

- Commit the allocation cleanup separately so performance regressions stay reviewable.

### Task 3: Add the benchmark project

**Files:**
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj`
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/BenchmarkWorldFactory.cs`
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs`
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/miniArch.sln`

**Step 1: Create the failing benchmark project**

- Add a BenchmarkDotNet project that references `MiniArch`.
- Add the Arch package dependency needed for the comparison runs.
- Add a shared factory that builds comparable worlds for both implementations.

**Step 2: Run a restore/build check**

Run: `dotnet build E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -v minimal`

Expected: fail until the project exists and is wired into the solution.

**Step 3: Write the minimal benchmark scaffolding**

- Add benchmark classes for query creation, add, set, remove, and destroy.
- Keep setup outside the measured methods.
- Make the benchmark inputs deterministic and comparable.

**Step 4: Run the build check again**

Run: `dotnet build E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -v minimal`

Expected: pass.

**Step 5: Commit**

- Keep benchmark scaffolding separate from runtime changes.

### Task 4: Establish the baseline comparison

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`
- Create: `E:/godot/arch/miniArch/scripts/benchmark.ps1`

**Step 1: Run the benchmark baseline**

Run: `dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release`

Expected: produce baseline measurements for MiniArch and Arch on the tracked operations.

**Step 2: Write the minimal benchmark harness**

- Normalize the benchmark scenarios so both implementations perform the same logical work.
- Capture the mean time and allocation data for each operation.
- Ensure the harness is stable enough to rerun after every optimization pass.

**Step 3: Run the benchmark again**

Run: `dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release`

Expected: report a reproducible baseline that can be compared across iterations.

**Step 4: Commit**

- Commit the benchmark harness and baseline setup before optimization begins.

### Task 5: Optimize the hot path for low GC

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Query.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/QueryIterators.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Chunk.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Archetype.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/*`

**Step 1: Pick the first regression to fix**

- Inspect the baseline output.
- Identify the largest avoidable allocation or slow path in the tracked operations.

**Step 2: Implement one focused optimization**

- Remove the highest-value allocation or unnecessary branch from the hot path.
- Keep the change small enough to validate against the benchmark immediately.

**Step 3: Re-run the benchmark**

Run: `dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release`

Expected: the targeted operation improves, or the change is reverted.

**Step 4: Repeat**

- Continue one optimization at a time until the allocation profile is stable and the average performance target is satisfied.

**Step 5: Commit**

- Use one commit per meaningful improvement so the performance story stays readable.

### Task 6: Update project knowledge

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/INDEX.md` if needed

**Step 1: Summarize the new behavior**

- Record the chainable query API.
- Record the filter semantics for `With`, `Without`, `Any`, and `Or`.
- Record the benchmark workflow and any new regression checkpoints.

**Step 2: Update the knowledge files**

- Keep each page single-topic.
- Preserve the “结论先行” structure.

**Step 3: Verify the index still matches**

Run: `Get-Content E:/godot/arch/miniArch/.knowledge/INDEX.md`

Expected: the index still points to the correct pages after the updates.

**Step 4: Commit**

- Commit the knowledge update after the code and benchmark work are complete.

