# GC Allocation Reduction Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reduce obvious steady-state GC in destroy-heavy world operations and command-buffer play by reusing internal scratch state while keeping public API and behavior unchanged.

**Architecture:** Introduce two narrow internal reuse points: a world-side destroy traversal scratch for subtree collection and a command-buffer-side compile scratch for temporary dedupe maps/sets. Keep all externally visible command/frame/query semantics unchanged and verify with allocation-focused tests plus MemoryDiagnoser benchmarks.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet, PowerShell

---

### Task 1: Add a failing allocation regression for warmed destroy cascade

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: Write the failing test**

- Add a test that prebuilds a batch of identical parent-child-grandchild worlds.
- Warm the destroy path once before measuring.
- Measure only repeated `world.Destroy(root)` execution on the current thread.
- Assert the warmed destroy cascade path allocates `0` bytes.

**Step 2: Run the targeted test to verify it fails**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~WorldLifecycleTests -v minimal
```

Expected: the new warmed destroy allocation test fails because destroy closure still allocates transient collections.

### Task 2: Implement world destroy traversal scratch reuse

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/HierarchyTable.cs`

**Step 1: Write the minimal implementation**

- Add an internal destroy traversal scratch holder for visited entities, destroy order, and traversal stack.
- Route `Destroy(...)`, `CollectCurrentDestroyClosure(...)`, and reverse-frame capture through the scratch holder.
- Ensure `HierarchyTable.CollectDestroySubtree(...)` uses the supplied scratch stack instead of allocating a new stack internally.
- Guard scratch cleanup with `try/finally`.

**Step 2: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldLifecycleTests|FullyQualifiedName~CommandBufferTests" -v minimal
```

Expected: destroy lifecycle tests pass, including the new allocation regression.

### Task 3: Add a failing allocation regression for warmed command-buffer play

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write the failing test**

- Add a dedicated-thread allocation test that warms one representative command-buffer play script, then measures repeated `Play()` on prebuilt buffers/worlds.
- Reuse the existing test style based on `GC.GetAllocatedBytesForCurrentThread()`.
- Assert the warmed play path allocates `0` bytes.

**Step 2: Run the targeted test to verify it fails**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

Expected: the new warmed play allocation test fails because compile still allocates temporary dictionaries/sets/lists.

### Task 4: Implement command-buffer compile scratch reuse

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs`

**Step 1: Write the minimal implementation**

- Add a buffer-local compile scratch object for the temporary dedupe maps/sets used by `Compile()`.
- Clear and reuse that scratch for each compile.
- Keep `Playback()`, `PlaybackDelta()`, `Play()`, and `PlayWithReverse()` behavior unchanged.
- If needed, add a narrow release step after `Compile()` consumers finish so scratch does not leak state.

**Step 2: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~WorldLifecycleTests|FullyQualifiedName~QueryTests" -v minimal
```

Expected: the new warmed play allocation regression passes and focused core tests remain green.

### Task 5: Measure benchmark allocation deltas

**Files:**
- Modify: none (unless a focused benchmark helper is needed after Task 4)

**Step 1: Run focused command buffer benchmark**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --filter "*CommandBuffer*"
```

Expected: MemoryDiagnoser reports lower allocations on MiniArch command-buffer workloads, especially play/replay-related paths.

**Step 2: Run focused query benchmark as regression guard**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --filter "*MiniArch_WithAll_Execute*"
```

Expected: no query regression is introduced while optimizing neighboring internals.
