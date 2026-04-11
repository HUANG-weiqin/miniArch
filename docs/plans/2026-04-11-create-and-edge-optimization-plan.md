# Create And Edge Optimization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce `Create` allocation, add bulk entity creation, and replace edge dictionaries with direct-index storage without regressing current structural-change benchmarks.

**Architecture:** `World` will gain explicit entity-capacity management so `Create` can pre-size metadata storage instead of relying on repeated `List<T>` growth. Bulk creation will reuse the same empty-signature archetype lookup and reserve path for many entities at once. `ArchetypeEdges` will move from dictionary lookups to lazily grown arrays keyed by `ComponentType.Value`, matching the rest of the direct-index runtime design.

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet.

---

### Task 1: Add entity-capacity management for `Create`

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: Write the failing tests**

- Add a test that `EnsureCapacity` raises world entity capacity before any creates.
- Add a test that a pre-sized world can create many entities while keeping all returned entities valid.

**Step 2: Run test to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorldLifecycleTests`

Expected: fail because `World` has no capacity API yet.

**Step 3: Write minimal implementation**

- Add a public `EnsureCapacity(int entityCapacity)` API.
- Add a public capacity surface for verification and tuning.
- Initialize metadata storage with a configurable starting capacity instead of the default zero-capacity `List<T>` growth path.

**Step 4: Run test to verify it passes**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorldLifecycleTests`

Expected: pass.

---

### Task 2: Add `CreateMany` bulk creation

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`

**Step 1: Write the failing tests**

- Add a test that `CreateMany` fills a supplied entity buffer with valid entities.
- Add a test that `CreateMany` respects entity ordering and location validity.

**Step 2: Run test to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorldLifecycleTests`

Expected: fail because `CreateMany` does not exist yet.

**Step 3: Write minimal implementation**

- Add `CreateMany(Span<Entity> entities)`.
- Reuse one empty archetype lookup and one upfront capacity check.
- Add benchmark methods for bulk create on both MiniArch and Arch using equivalent empty-signature world setup.

**Step 4: Run test and benchmark to verify it passes**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorldLifecycleTests`

Run: `dotnet run -c Release --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -- --filter "*CreateMany*"`

Expected: tests pass and the benchmark runs successfully.

---

### Task 3: Replace `ArchetypeEdges` dictionaries with direct-index arrays

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/ArchetypeEdges.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ArchetypeTests.cs`

**Step 1: Write the failing test**

- Add a test that edge caching still works when component ids are sparse or discovered later.

**Step 2: Run test to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ArchetypeTests`

Expected: fail until the new edge storage supports late growth.

**Step 3: Write minimal implementation**

- Store add/remove edges in lazily grown `Archetype?[]` buffers.
- Keep the public `TryGetAdd/TryGetRemove/CacheAdd/CacheRemove` surface unchanged.

**Step 4: Run tests and structural benchmarks**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore`

Run: `dotnet run -c Release --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -- --filter "*StructuralChangeBenchmarks*"`

Expected: tests pass, `Create` allocation improves, and structural-change timings do not regress.

---

### Task 4: Update docs and verify the final state

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/INDEX.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Update knowledge**

- Document entity-capacity tuning, bulk creation, and direct-index edge storage.

**Step 2: Run final verification**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore`

Run: `dotnet run -c Release --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -- --filter "*StructuralChangeBenchmarks*"`

Expected: all tests pass and the benchmark suite provides fresh evidence for `Create`, `Add`, `Set`, `Remove`, and `Destroy`.
