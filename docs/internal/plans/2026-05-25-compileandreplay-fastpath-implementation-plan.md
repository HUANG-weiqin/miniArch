# CompileAndReplay Fast Path Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `CommandBuffer.CompileAndReplay()` faster for same-world replay without changing retained `FrameDelta` or public cross-world replay semantics.

**Architecture:** Keep `FrameDelta` as the public compiled IR, add final signature data to created-entity records, and introduce an internal trusted replay path used only by `CompileAndReplay()`. Leave `World.Replay(FrameDelta)` behavior intact.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet

---

### Task 1: Lock fast-path behavior with tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Test: `tests/MiniArch.Tests/Core/CommandBufferGcVerificationTests.cs`

**Step 1: Write the failing tests**

- Add one focused test that verifies `Compile()` still produces a retained frame that replays correctly after the created-entity shape change.
- Add one focused test that verifies `CompileAndReplay()` still matches `Compile()+Replay()` on a create-heavy frame.

**Step 2: Run tests to verify the baseline**

Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~CommandBufferTests"`

Expected: existing tests pass before the implementation edit.

**Step 3: Keep GC smoke coverage in scope**

- Confirm `CommandBufferGcVerificationTests` still covers `CompileAndReplay()` create/set/add/remove/destroy cases.

**Step 4: Run the GC-sensitive tests**

Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~CommandBufferGcVerificationTests"`

Expected: pass before and after implementation.

### Task 2: Carry final signature in compiled created entities

**Files:**
- Modify: `src/MiniArch/Core/FrameDelta.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Test: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Extend `RawCreatedEntity`**

- Add a `Signature Signature` field to `RawCreatedEntity`.

**Step 2: Build the final signature once in compile**

- In `CreatedEntityState.ToCompiledEntity()`, after sorting the components by `ComponentType`, create the final `Signature` from the sorted unique component types.
- Return the signature together with the sorted `RawComponentValue[]`.

**Step 3: Update any compile/rebuild helpers**

- Adjust any `FrameDelta` copy / merge code that constructs `RawCreatedEntity` so the new field is preserved.

**Step 4: Run command-buffer tests**

Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~CommandBufferTests"`

Expected: pass.

### Task 3: Add the trusted same-world replay path

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Reference: `src/MiniArch/Core/FrameDelta.cs`

**Step 1: Add an internal replay entry for trusted compiled batches**

- Implement an internal `World` method used only by `CommandBuffer.CompileAndReplay()`.
- Keep it near `Replay(FrameDelta)` so the ordering logic stays easy to diff.

**Step 2: Reuse compiled metadata directly**

- Materialize created entities from the precomputed `RawCreatedEntity.Signature`.
- Reuse stored `ComponentType` for add/set/remove commands when replaying to the owning world.
- Avoid `EnsureReplayReservation(...)` in this trusted path.

**Step 3: Switch `CompileAndReplay()` to the new path**

- Replace `_world.Replay(compiled)` with the new internal fast path call.
- Keep `Compile()` on the public `FrameDelta` path.

**Step 4: Run focused tests**

Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~CommandBufferTests"`

Expected: pass.

### Task 4: Verify throughput and GC regression gates

**Files:**
- Reference: `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Reference: `benchmarks/MiniArch.Benchmarks/Program.cs`

**Step 1: Run GC-sensitive verification**

Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~CommandBufferGcVerificationTests"`

Expected: pass.

**Step 2: Run command-buffer benchmark**

Run: `dotnet run --project .\benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj -c Release -- command-buffer --full --filter "*MiniArch_CommandBuffer_RecordPlay*"`

Expected: `MiniArch` create-heavy / dense-existing / mixed-script medians stay correct and improve or at least do not regress.

**Step 3: Record the measured deltas**

- Compare the new numbers against the current baseline from 2026-05-25 before finalizing.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/CommandBuffer.cs src/MiniArch/Core/FrameDelta.cs src/MiniArch/Core/World.cs tests/MiniArch.Tests/Core/CommandBufferTests.cs docs/plans/2026-05-25-compileandreplay-fastpath-design.md docs/plans/2026-05-25-compileandreplay-fastpath-implementation-plan.md
git commit -m "perf: speed up CompileAndReplay fast path"
```
