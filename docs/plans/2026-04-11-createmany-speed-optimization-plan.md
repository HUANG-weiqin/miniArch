# CreateMany Speed Optimization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce `CreateMany(empty)` time by replacing per-entity reserve work with chunk-batched reservation, without regressing existing structural-change benchmarks.

**Architecture:** `World.CreateMany` will stop calling `Archetype.ReserveEntity` once per entity. Instead, `Archetype` will expose a batch reservation path that fills consecutive rows chunk by chunk, and returns enough placement information for `World` to write entities and locations in one linear pass. This keeps storage policy inside `Archetype` while letting `World` avoid repeated writable-chunk scans and repeated boundary decisions.

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet.

---

### Task 1: Lock the desired batch semantics with tests

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: Write the failing tests**

- Add a test that `CreateMany` preserves row continuity across chunk boundaries for the empty archetype.
- Add a test that a second `CreateMany` call appends after existing entities instead of corrupting prior locations.

**Step 2: Run test to verify it fails or lacks coverage**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorldLifecycleTests`

Expected: the new assertions fail or expose that the current implementation only passes by repeatedly taking the slow single-entity path.

**Step 3: Keep the tests as the behavioral guardrail**

- Do not change public semantics.
- Keep the tests focused on location/order correctness, not implementation details.

---

### Task 2: Add batch reservation support to `Archetype`

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Archetype.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Chunk.cs`

**Step 1: Add the minimal batch API**

- Add an internal batch reservation method on `Archetype` that reserves `n` empty slots across existing/new chunks.
- Keep chunk creation and writable-space policy inside `Archetype`.

**Step 2: Use chunk-sized batches**

- Fill the current writable chunk to capacity before moving to the next.
- Create new chunks only when the remaining amount cannot fit in already allocated writable chunks.

**Step 3: Return enough placement data**

- Return contiguous placement ranges or equivalent chunk/row descriptors so `World` can fill entities and locations without re-querying writable chunks.

**Step 4: Preserve single-entity hot paths**

- Do not slow down `Create`, `Add`, `Set`, `Remove`, or `Destroy`.
- Keep `ReserveEntity` behavior unchanged for existing callers.

---

### Task 3: Rewrite `World.CreateMany` around batch reservation

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`

**Step 1: Keep upfront capacity management**

- Retain the current `EnsureCapacity` logic for metadata arrays.
- Reuse a single empty-archetype lookup per call.

**Step 2: Separate id acquisition from placement fill**

- Continue using the current id/version model.
- Write `entities[i]` and `_locations[id]` from batch placement data in a single forward loop.

**Step 3: Minimize repeated work**

- Avoid per-entity writable-chunk scans.
- Avoid per-entity chunk lookups after placement has already been decided.

---

### Task 4: Verify the speed target and guard regressions

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs` only if benchmark coverage needs adjustment
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Run targeted tests**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~WorldLifecycleTests`

Expected: pass.

**Step 2: Run full tests**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --no-restore`

Expected: pass.

**Step 3: Run structural benchmark evidence**

Run: `dotnet run -c Release --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -- --filter "*StructuralChangeBenchmarks.Arch_CreateMany_Entity*" "*StructuralChangeBenchmarks.MiniArch_CreateMany_Entity*" "*StructuralChangeBenchmarks.Arch_Create_Entity*" "*StructuralChangeBenchmarks.MiniArch_Create_Entity*" "*StructuralChangeBenchmarks.Arch_Add_Position*" "*StructuralChangeBenchmarks.MiniArch_Add_Position*" "*StructuralChangeBenchmarks.Arch_Set_Position*" "*StructuralChangeBenchmarks.MiniArch_Set_Position*" "*StructuralChangeBenchmarks.Arch_Remove_Position*" "*StructuralChangeBenchmarks.MiniArch_Remove_Position*" "*StructuralChangeBenchmarks.Arch_Destroy_Entity*" "*StructuralChangeBenchmarks.MiniArch_Destroy_Entity*"`

Expected: `CreateMany(empty)` time improves materially, and the other structural operations do not regress.

**Step 4: Update knowledge**

- Record the chunk-batched `CreateMany` path in `kb-core-ecs.md`.
- Record the benchmark interpretation and regression guardrail in `kb-test-workflow.md`.
