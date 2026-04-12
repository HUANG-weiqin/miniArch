# MiniArch API Unification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Unify the public API around a single `MiniArch` user namespace, remove duplicate `World`/`Entity`/`QueryDescription` concepts, and delete typed query façades in favor of `QueryDescription`-driven traversal.

**Architecture:** Move the user-facing core concepts to `MiniArch`, teach advanced query execution to consume the single shared `QueryDescription`, then delete the old `MiniArch.Ecs` and generic query convenience surface. Update tests, benchmarks, README, and knowledge docs to the new contract.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet

---

### Task 1: Lock the new namespace and query contract with failing tests

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing tests**

- Rename user API tests to use `using MiniArch;`
- Delete typed-query usage from tests
- Add/adjust tests for:
  - `MiniArch.World` exists in the root namespace
  - `MiniArch.QueryDescription` drives `foreach` entity traversal
  - `TryGet<T>` still works from the root namespace world
  - warmed description-based enumeration stays allocation-free

**Step 2: Run test to verify it fails**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests -v minimal`

Expected: FAIL because root namespace types and description-only query contract are not implemented yet.

**Step 3: Confirm failure reason**

- Missing `MiniArch.World`
- Missing `MiniArch.QueryDescription`
- Old typed query assertions no longer compile or pass

### Task 2: Move the single user-facing core concepts to `MiniArch`

**Files:**
- Modify: `src/MiniArch/Ecs/World.cs`
- Modify: `src/MiniArch/Ecs/Entity.cs`
- Modify: `src/MiniArch/Ecs/QueryDescription.cs`

**Step 1: Rename namespaces**

- Change `MiniArch.Ecs` to `MiniArch` for:
  - `World`
  - `Entity`
  - `QueryDescription`

**Step 2: Remove wrapper dependence on `MiniArch.Core.QueryDescription` duplication**

- Turn the root `QueryDescription` into the only public description type
- Keep only the internal bridges needed by the advanced layer

**Step 3: Run targeted tests**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests.Description_based -v minimal`

Expected: still failing until query internals are updated, but namespace errors should move forward.

### Task 3: Make advanced query consume the shared `MiniArch.QueryDescription`

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/Query.cs`
- Modify: `src/MiniArch/Core/QueryBuilder.cs`
- Modify: `src/MiniArch/Core/QueryDescription.cs`

**Step 1: Replace duplicate description usage**

- Update `MiniArch.Core.World.Query(in ...)` to consume the root `MiniArch.QueryDescription`
- Update internal caches/filter translation to use the root description type

**Step 2: Remove `MiniArch.Core.QueryDescription` from the public surface**

- Delete or internalize the duplicate core description type

**Step 3: Run targeted tests**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter QueryTests -v minimal`

Expected: description-based query tests pass or narrow to generic-query breakages.

### Task 4: Delete typed query façades and generic query convenience APIs

**Files:**
- Delete: `src/MiniArch/Ecs/Query.cs`
- Modify: `src/MiniArch/Ecs/World.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/Query.cs`
- Modify: `src/MiniArch/Core/QueryBuilder.cs`

**Step 1: Delete user typed query surface**

- Remove:
  - `Query<T>`
  - `Query<T1, T2>`
  - `QueryItem<T>`
  - `QueryItem<T1, T2>`
  - `QueryEnumerator<T>`
  - `QueryEnumerator<T1, T2>`

**Step 2: Delete generic world query shortcuts**

- Remove:
  - `World.Query()`
  - `World.Query<T1>()`
  - `World.Query<T1, T2>()`
  - `World.Query<T1, T2, T3>()`

**Step 3: Delete generic query mutation helpers**

- Remove:
  - `Query.With<T>()`
  - `Query.Without<T>()`
  - `Query.Any<T>()`
  - `Query.Or<T>()`
- Remove `QueryBuilder` if no longer needed

**Step 4: Run focused tests**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter "UserQueryTests|QueryTests" -v minimal`

Expected: user tests green; remaining failures show downstream call sites needing migration.

### Task 5: Migrate tests and benchmarks to description-only querying

**Files:**
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`
- Modify: `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`
- Modify: `benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs`
- Modify: other benchmark/test files found by compile errors

**Step 1: Replace builder/generic query calls**

- Convert all query construction to:
  - `new QueryDescription().With<T>()...`
  - `world.Query(in description)`

**Step 2: Replace typed query assertions**

- Rewrite tests to enumerate entities and read components via `TryGet<T>` or advanced chunk APIs as appropriate

**Step 3: Run test to verify it passes**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj -v minimal`

Expected: PASS

### Task 6: Update README and knowledge docs to the new API boundary

**Files:**
- Modify: `README.md`
- Modify: `src/MiniArch/README.md`
- Modify: `.knowledge/kb-user-api-layering.md`
- Modify: `.knowledge/INDEX.md`

**Step 1: Rewrite API guide**

- Default user namespace is `MiniArch`
- Advanced namespace is `MiniArch.Core`
- Only one `World` / `Entity` / `QueryDescription`
- Query is description-only

**Step 2: Update knowledge contract**

- Reflect the new boundary
- Remove stale references to `MiniArch.Ecs` and typed query façade

**Step 3: Run verification**

Run: `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1`

Expected: build and tests pass.
