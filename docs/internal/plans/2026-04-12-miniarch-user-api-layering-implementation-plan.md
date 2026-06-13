# MiniArch User API Layering Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a user-facing `MiniArch.Ecs` API layer with foreach-friendly queries while keeping `MiniArch.Core` as the advanced API.

**Architecture:** Add thin facade types in `MiniArch.Ecs`, keep `MiniArch.Core` behavior intact, and bridge them with zero-allocation struct enumerators over existing chunk snapshots and typed columns.

**Tech Stack:** C# 12, .NET 8, xUnit

---

### Task 1: Lock the desired user-facing query behavior with tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`
- Create: `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`

**Step 1: Write the failing tests**

- Add tests for:
  - `foreach (var item in world.Query<Position>())`
  - `foreach (var item in world.Query<Position, Velocity>())`
  - `world.TryGet<T>(entity, out component)` on the new user-facing world
  - warmed user query enumeration allocating `0` bytes on current thread

**Step 2: Run test to verify it fails**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests -v minimal`

**Step 3: Confirm failure reason**

- Missing `MiniArch.World`
- Missing user query/result types
- Missing `TryGet`

### Task 2: Add the user-facing namespace and entity/world facade

**Files:**
- Create: `src/MiniArch/Ecs/Entity.cs`
- Create: `src/MiniArch/Ecs/World.cs`
- Modify: `src/MiniArch/MiniArch.csproj`

**Step 1: Write minimal facade**

- Add `MiniArch.Entity`
- Add `MiniArch.World`
- Forward `Create`, `Add`, `Set`, `Remove`, `Destroy`
- Add `TryGet<T>`
- Add `Advanced` property exposing underlying `MiniArch.Core.World`

**Step 2: Run targeted tests**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests.TryGet -v minimal`

### Task 3: Add internal core helpers required by facade query

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/Chunk.cs`

**Step 1: Add minimal helpers**

- Add direct component read path by entity for `TryGet<T>`
- Add internal access to current entity/component arrays for zero-copy enumeration

**Step 2: Run targeted tests**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests.TryGet -v minimal`

### Task 4: Implement user-facing generic query wrappers and enumerators

**Files:**
- Create: `src/MiniArch/Ecs/Query.cs`

**Step 1: Implement single-component query**

- Add `MiniArch.Query<T>`
- Add `QueryItem<T>`
- Add struct enumerator using matched chunks + typed arrays

**Step 2: Implement dual-component query**

- Add `MiniArch.Query<T1, T2>`
- Add `QueryItem<T1, T2>`
- Add struct enumerator using matched chunks + two typed arrays

**Step 3: Wire facade world query methods**

- `World.Query<T>()`
- `World.Query<T1, T2>()`

**Step 4: Run focused tests**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests.Query -v minimal`

### Task 5: Verify no-allocation foreach path and no core regression

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs` if needed

**Step 1: Warm query and measure allocations**

- Use `GC.GetAllocatedBytesForCurrentThread()`
- Pre-warm query before measured loop
- Assert no extra allocation in the enumeration body

**Step 2: Run user API test suite**

Run: `dotnet test tests\MiniArch.Tests\MiniArch.Tests.csproj --filter UserQueryTests -v minimal`

**Step 3: Run full test suite**

Run: `./scripts/test.ps1`

### Task 6: Document the API boundary and migration path

**Files:**
- Modify: `src/MiniArch/README.md`
- Create or Modify: `.knowledge/kb-user-api-layering.md`
- Modify: `.knowledge/INDEX.md`

**Step 1: Document ordinary vs advanced API**

- `MiniArch` is user-facing
- `MiniArch.Core` is advanced
- Show `foreach` query examples
- Explain migration from `Query().With<T>().Build()` and chunk-row loops

**Step 2: Re-run verification**

Run: `./scripts/test.ps1`
