# FrameDelta Rename & Obsolete Type Removal

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Rename `CompiledCommandBatch` → `FrameDelta`, remove all obsolete boxed types (`FrameCommands`, `WorldDelta`, `ReverseFrameCommands`), and clean up all dependent code.

**Architecture:** `FrameDelta` (was `CompiledCommandBatch`) becomes the single unified delta representation. All boxed intermediate types are removed. The public API simplifies to `CommandBuffer.Playback()` → `FrameDelta` + `World.Replay(FrameDelta)`.

**Tech Stack:** C# / Godot 4.4 / .NET

---

## Overview of Removed Types

From `FrameCommands.cs` (entire file deleted, some types moved):
- `FrameCommands` struct + `FrameCommandsState` class → DELETED
- `ReverseFrameCommands` struct + `ReverseFrameCommandsState` class → DELETED
- `FrameComponentValue` record struct → DELETED
- `FrameCreatedEntity` record struct → DELETED
- `ReverseFrameEntity` record struct → DELETED
- `FrameEntityComponentCommand` record struct → DELETED
- `FrameEntityRemoveCommand` record struct → DELETED
- `FrameAddChildCommand` → MOVE to FrameDelta.cs
- `FrameUnAddChildCommand` → MOVE to FrameDelta.cs
- `RecordedHierarchyCommand` → MOVE to FrameDelta.cs (internal)
- `RecordedRawCommand` → MOVE to FrameDelta.cs (internal)
- `RecordedRemoveCommand` → MOVE to FrameDelta.cs (internal)

From `WorldDelta.cs` (entire file deleted):
- `WorldDelta` struct + `WorldDeltaState` class → DELETED
- `WorldDeltaEntry` record struct → DELETED
- `WorldEntityPublicState` record struct → DELETED

From `CommandBuffer.cs`:
- `CompiledCommandBatch` → RENAME to `FrameDelta`, move to FrameDelta.cs, make public
- `CompiledRawComponentValue` → MOVE to FrameDelta.cs, make public
- `CompiledRawCreatedEntity` → MOVE to FrameDelta.cs, make public
- `CompiledRawComponentCommand` → MOVE to FrameDelta.cs, make public
- `CompiledRemoveCommand` → MOVE to FrameDelta.cs, make public
- `CompiledRawCreatedEntity.ToFrame()` → DELETED
- `CompiledRawComponentCommand.ToFrame()` → DELETED
- `CompiledRemoveCommand.ToFrame()` → DELETED
- `CompiledCommandBatch.ToFrameCommands()` → DELETED
- `CommandBuffer.PlaybackDelta()` → DELETED
- `CommandBuffer.PlayWithReverse()` → DELETED

From `World.cs` (methods to remove):
- `Replay(in FrameCommands)` (boxed overload) → DELETED
- `ReplayWithReverse(in FrameCommands)` → DELETED
- `ApplyDeltaForward(in WorldDelta)` → DELETED
- `ApplyDeltaBackward(in WorldDelta)` → DELETED
- `Rewind(in ReverseFrameCommands)` → DELETED
- `CaptureDelta(in FrameCommands)` → DELETED
- `CaptureReverseFrameCommands(in FrameCommands, Dictionary<Type, ComponentType>)` → DELETED
- `TryCaptureEntityPublicState` → DELETED
- `MaterializeDeltaEntity` → DELETED
- `SynchronizeEntityComponents` → DELETED
- `EnsureDeltaEntitySlot` → DELETED
- `RemoveFreeIdById` → DELETED
- `NormalizeComponentValues` → DELETED
- `GetComponentSortKey` → DELETED
- `CollectCurrentDestroyClosure` → DELETED
- `CollectShadowDestroyClosure` → DELETED
- `PublicStatesEqual` → DELETED
- `RestoreDestroyedEntity` → DELETED
- `RestoreReservedEntity` → DELETED
- `CaptureReverseComponentMutation` → DELETED
- `CaptureReverseRemove` → DELETED
- `CaptureDestroyedEntity` → DELETED
- `GetHierarchyDepth` → DELETED
- `CaptureReverseLink` → DELETED
- `CaptureReverseUnlink` → DELETED
- `DeltaEntityState` inner class → DELETED
- `MaterializeReservedEntity` overload taking `IReadOnlyList<FrameComponentValue>` → DELETED (keep raw byte overload)
- `BuildReplaySignature` overload taking `IReadOnlyList<FrameComponentValue>` → DELETED (keep raw byte overload)

World.cs scratch fields to remove:
- `_shadowDestroyOrderScratch`
- `_shadowDestroyStackScratch`
- `_shadowDestroyVisitedScratch`

World.cs methods to UPDATE (not delete):
- `Replay(CommandBuffer.CompiledCommandBatch)` → `Replay(FrameDelta)`, make public, update internal type references

---

### Task 1: Create FrameDelta.cs

**Files:**
- Create: `src/MiniArch/Core/FrameDelta.cs`

Create `src/MiniArch/Core/FrameDelta.cs` with the following content:

```csharp
using System.Buffers;

namespace MiniArch.Core;

public sealed class FrameDelta
{
    public List<Entity> ReservedEntities { get; } = new(4);
    public List<CompiledRawCreatedEntity> CreatedEntities { get; } = new(4);
    public List<FrameAddChildCommand> AddChildCommands { get; } = new(4);
    public List<FrameUnAddChildCommand> UnAddChildCommands { get; } = new(4);
    public List<CompiledRawComponentCommand> AddCommands { get; } = new(4);
    public List<CompiledRawComponentCommand> SetCommands { get; } = new(4);
    public List<CompiledRemoveCommand> RemoveCommands { get; } = new(4);
    public List<Entity> DestroyedEntities { get; } = new(4);
    public List<Entity> ReleasedEntities { get; } = new(4);

    public void Clear()
    {
        ReservedEntities.Clear();
        CreatedEntities.Clear();
        AddChildCommands.Clear();
        UnAddChildCommands.Clear();
        AddCommands.Clear();
        SetCommands.Clear();
        RemoveCommands.Clear();
        DestroyedEntities.Clear();
        ReleasedEntities.Clear();
    }

    public bool IsEmpty =>
        ReservedEntities.Count == 0 &&
        CreatedEntities.Count == 0 &&
        AddChildCommands.Count == 0 &&
        UnAddChildCommands.Count == 0 &&
        AddCommands.Count == 0 &&
        SetCommands.Count == 0 &&
        RemoveCommands.Count == 0 &&
        DestroyedEntities.Count == 0 &&
        ReleasedEntities.Count == 0;
}

public readonly record struct CompiledRawComponentValue(
    int ComponentTypeId,
    Type RuntimeType,
    ComponentType ComponentType,
    int ComponentSize,
    byte[] Data,
    int DataOffset,
    int DataSize);

public readonly record struct CompiledRawCreatedEntity(Entity Entity, Signature? Signature, CompiledRawComponentValue[] Components);

public readonly record struct CompiledRawComponentCommand(
    Entity Entity,
    int ComponentTypeId,
    Type RuntimeType,
    ComponentType ComponentType,
    int DataOffset,
    int DataSize,
    ComponentWriterCache.ColumnWriterDelegate? ColumnWriter,
    byte[] Data);

public readonly record struct CompiledRemoveCommand(Entity Entity, int ComponentTypeId, Type RuntimeType, ComponentType ComponentType);

public readonly record struct FrameAddChildCommand(Entity Parent, Entity Child);

public readonly record struct FrameUnAddChildCommand(Entity Child);

internal readonly record struct RecordedHierarchyCommand(Entity Child, Entity Parent, bool IsLink);

internal readonly record struct RecordedRawCommand(Entity Entity, int ComponentTypeId, int DataOffset, int DataSize);

internal readonly record struct RecordedRemoveCommand(Entity Entity, int ComponentTypeId);
```

### Task 2: Modify CommandBuffer.cs

**Files:**
- Modify: `src/MiniArch/Core/CommandBuffer.cs`

Remove all the inner type definitions that were moved to FrameDelta.cs:
- Remove `CompiledRawComponentValue` record struct (lines ~554-567)
- Remove `CompiledRawCreatedEntity` record struct (lines ~569-590, including `ToFrame()` method)
- Remove `CompiledRawComponentCommand` record struct (lines ~592-607, including `ToFrame()` method)
- Remove `CompiledRemoveCommand` record struct (lines ~609-615, including `ToFrame()` method)
- Remove `CompiledCommandBatch` class (lines ~617-700, including `ToFrameCommands()` method)

Update type references:
- `_compiledBatch` field type: `CompiledCommandBatch` → `FrameDelta`
- `Compile()` return type: `CompiledCommandBatch` → `FrameDelta`

Remove methods:
- `PlaybackDelta()` (lines ~117-127)
- `PlayWithReverse()` (lines ~152-168)

Update `Playback()`:
```csharp
public FrameDelta Playback()
{
    var compiled = Compile();
    Clear();
    return compiled;
}
```

Also remove the `ReadBoxed()` method that was on CompiledRawComponentValue (it uses ComponentWriterCache.ReadBoxed which is fine to leave as a static utility).

### Task 3: Modify World.cs

**Files:**
- Modify: `src/MiniArch/Core/World.cs`

Update `Replay(CommandBuffer.CompiledCommandBatch)` signature:
```csharp
public void Replay(FrameDelta delta)
```
Change `internal` to `public`. Inside the method, replace `compiledCommands` parameter name with `delta`. Update all references within.

Remove all methods listed in the "methods to remove" section above. This includes approximately lines 1557-1616 (boxed Replay), 1621-1708 (ReplayWithReverse, ApplyDeltaForward/Backward, Rewind), 1774-1911 (CaptureDelta), 1998-2278 (helpers), 2280-2503 (CaptureReverseFrameCommands and helpers), 2505-2557 (RestoreDestroyedEntity, RestoreReservedEntity), and the FrameComponentValue overloads of MaterializeReservedEntity and BuildReplaySignature.

Remove scratch fields:
- `_shadowDestroyOrderScratch`
- `_shadowDestroyStackScratch`
- `_shadowDestroyVisitedScratch`

Remove `DeltaEntityState` inner class (lines ~2231-2278).

Update type references:
- `CommandBuffer.CompiledRawComponentValue` → `CompiledRawComponentValue`
- `CommandBuffer.CompiledRawCreatedEntity` → `CompiledRawCreatedEntity`
- `CommandBuffer.CompiledRawComponentCommand` → `CompiledRawComponentCommand`

### Task 4: Delete obsolete files

**Files:**
- Delete: `src/MiniArch/Core/FrameCommands.cs`
- Delete: `src/MiniArch/Core/WorldDelta.cs`

### Task 5: Update tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs`

**Rules for test changes:**
1. Tests that call `buffer.Playback()` → `world.Replay(frame)` where `frame` is `FrameCommands`: update to use `FrameDelta`. The flow stays the same: `var delta = buffer.Playback(); world.Replay(delta);`
2. Tests that ONLY use `WorldDelta`, `ReverseFrameCommands`, `ApplyDeltaForward/Backward`, `ReplayWithReverse`, `CaptureDelta`, `PlayWithReverse`, `PlaybackDelta`, or `Rewind(ReverseFrameCommands)`: **DELETE these entire test methods**.
3. Tests that use `buffer.Play()` (returns bool): keep as-is, no changes needed.

**Test methods to DELETE from CommandBufferTests.cs:**
Any test that uses any of: `PlaybackDelta`, `WorldDelta`, `ApplyDeltaForward`, `ApplyDeltaBackward`, `ReverseFrameCommands`, `ReplayWithReverse`, `PlayWithReverse`, `Rewind(ReverseFrameCommands)`.

**Test methods to UPDATE from CommandBufferTests.cs:**
Tests that use `Playback()` + `Replay(in FrameCommands)` but NOT the removed APIs. Change to `Playback()` → `FrameDelta` + `Replay(FrameDelta)`.

**ThroughputRunnerTests.cs:** Check what it uses and update/remove accordingly.

### Task 6: Update benchmarks

**Files:**
- Modify: `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `benchmarks/MiniArch.Benchmarks/CommandBufferReplayScenarios.cs`
- Modify: `benchmarks/MiniArch.Benchmarks/ThroughputRunner.cs`
- Modify: `benchmarks/MiniArch.Benchmarks/Program.cs`

Same rules as tests: remove benchmarks that depend on removed types, update those that can be converted.

### Task 7: Update knowledge base

**Files:**
- Modify: `.knowledge/INDEX.md`
- Modify: `.knowledge/kb-command-buffer-feasibility.md`
- Modify: `.knowledge/kb-core-ecs.md`
- Modify: `.knowledge/kb-hierarchy-runtime.md`
- Modify: `.knowledge/kb-test-workflow.md`

Update all references from `CompiledCommandBatch` → `FrameDelta`, remove references to deleted types.

### Task 8: Build and verify

Run: `dotnet build`
Run: `dotnet test`

Expected: All builds succeed, all remaining tests pass.
