# CommandBuffer Struct Shrink (Phase 2) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Shrink `EntityOpSlot` and `CreatedComponent` by removing redundant fields that duplicate InlineMap keys, reducing per-entity command-buffer memory footprint by ~30%.

**Architecture:** Add key-value pair iteration to `InlineMap` (zero-allocation, `ref readonly` based). Then remove `ComponentType` from `CreatedComponent` and `ComponentTypeId` from `EntityOpSlot`, reconstructing identity from the map key at emit/submit time. Avoid `[StructLayout.Explicit]` union unless a simpler sequential layout can't achieve the same size.

**Tech Stack:** C# / .NET 8, Unsafe code, ArrayPool, HeroPipeline perf gate.

---

### Task 1: Add InlineMap pair-iteration API

**Rationale:** Currently InlineMap only exposes value iteration (Value0..Value3 + overflow). After removing duplicated component identity from values, emit/submit paths need the key too. This is a non-breaking API addition.

**Files:**
- Modify: `src/MiniArch/Core/InlineMap.cs`

**Step 1: Add `ForEach` method to InlineMap**

Add a method that calls a delegate with `(TKey key, in TValue value)` for each entry. The delegate-based approach avoids copying values (uses `in`) and avoids allocating tuples.

```csharp
/// <summary>
/// Iterates all entries, calling the handler with key and readonly reference to value.
/// </summary>
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void ForEach<THandler>(ref THandler handler, ref OverflowPool<TKey, TValue> pool)
    where THandler : struct, IInlineMapHandler<TKey, TValue>
{
    if (Count >= 1) handler.Handle(Key0, in Value0);
    if (Count >= 2) handler.Handle(Key1, in Value1);
    if (Count >= 3) handler.Handle(Key2, in Value2);
    if (Count >= 4) handler.Handle(Key3, in Value3);
    if (OverflowCount > 0)
    {
        for (var nodeIdx = OverflowHead; nodeIdx >= 0; nodeIdx = pool.GetNext(nodeIdx))
        {
            handler.Handle(pool.GetKeyReadonly(nodeIdx), in pool.GetValueReadonly(nodeIdx));
        }
    }
}
```

Wait — generic struct handler interface requires a new interface. That's overhead. Simpler: just expose the key fields directly (they're already public). The call sites can read keys inline. No new API needed — keys are already public (`Key0`, `Key1`, `Key2`, `Key3`).

**Revised Step 1: No new API needed.** InlineMap keys are already public fields. Call sites will read `KeyN` alongside `ValueN`. Mark this task as done — no code change required.

**Step 2: Verify no change needed**

Confirm that all InlineMap iteration sites can directly access KeyN:
- `existingOps.Key0` / `existingOps.Key1` / etc. are public fields — YES, they're public.

**Step 3: Commit (if any changes were needed — skip if not)**

No commit needed for this task.

---

### Task 2: Remove `ComponentType` from `CreatedComponent`

**Rationale:** `CreatedComponent` is stored in `InlineMap<int, CreatedComponent>`. The map key (int) equals `ComponentType.Value`. `ComponentType` in the struct is 100% redundant. Removing it shrinks `CreatedComponent` from 16B to 12B, and the InlineMap from 92B to 76B.

**Files:**
- Modify: `src/MiniArch/Core/CommandBuffer.cs` — struct definition, `ExtractAndSortComponents`, `BuildCreatedEntityComponentsForDelta`, `BuildCreatedEntityComponentsFromFrozen`, `SubmitFromFrozen` raw components construction, `CopyComponentFromArchetype`
- Test: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write a failing test that asserts struct size**

This is optional but helps verify the optimization. Skip — we'll verify via perf numbers.

**Step 2: Change `CreatedComponent` definition**

In `CommandBuffer.cs`, change:
```csharp
internal readonly record struct CreatedComponent(ComponentType ComponentType, int SlabIndex, int DataOffset, int DataSize);
```
to:
```csharp
internal readonly record struct CreatedComponent(int SlabIndex, int DataOffset, int DataSize);
```

**Step 3: Fix `ExtractAndSortComponents` — read ComponentType from InlineMap key**

Currently (line 772-782):
```csharp
if (state.Map.Count >= 1) { sources[idx] = state.Map.Value0; types[idx] = state.Map.Value0.ComponentType; idx++; }
if (state.Map.Count >= 2) { sources[idx] = state.Map.Value1; types[idx] = state.Map.Value1.ComponentType; idx++; }
if (state.Map.Count >= 3) { sources[idx] = state.Map.Value2; types[idx] = state.Map.Value2.ComponentType; idx++; }
if (state.Map.Count >= 4) { sources[idx] = state.Map.Value3; types[idx] = state.Map.Value3.ComponentType; idx++; }
for (var nodeIdx = state.Map.OverflowHead; nodeIdx >= 0; nodeIdx = pool.GetNext(nodeIdx))
{
    var val = pool.GetValueReadonly(nodeIdx);
    sources[idx] = val;
    types[idx] = val.ComponentType;
    idx++;
}
```

Change to:
```csharp
if (state.Map.Count >= 1) { sources[idx] = state.Map.Value0; types[idx] = new ComponentType(state.Map.Key0); idx++; }
if (state.Map.Count >= 2) { sources[idx] = state.Map.Value1; types[idx] = new ComponentType(state.Map.Key1); idx++; }
if (state.Map.Count >= 3) { sources[idx] = state.Map.Value2; types[idx] = new ComponentType(state.Map.Key2); idx++; }
if (state.Map.Count >= 4) { sources[idx] = state.Map.Value3; types[idx] = new ComponentType(state.Map.Key3); idx++; }
for (var nodeIdx = state.Map.OverflowHead; nodeIdx >= 0; nodeIdx = pool.GetNext(nodeIdx))
{
    sources[idx] = pool.GetValueReadonly(nodeIdx);
    types[idx] = new ComponentType(pool.GetKeyReadonly(nodeIdx));
    idx++;
}
```

Note: `ComponentType` has `explicit operator ComponentType(int value)` so we can also just cast: `(ComponentType)state.Map.Key0`. Use whichever is more readable.

**Step 4: Fix all `CreatedComponent` construction sites**

All places that construct `new CreatedComponent(info.ComponentType, slabIndex, offset, info.Size)` change to `new CreatedComponent(slabIndex, offset, info.Size)`.

Search for all occurrences:
- `state.Map.Set(componentTypeId, new CreatedComponent(info.ComponentType, ...)` → `new CreatedComponent(slabIndex, offset, info.Size)`
- `state.Map.Set(componentTypeId, new CreatedComponent(info.RuntimeType, info.ComponentType, ...)` → already cleaned up, should only have `info.ComponentType` now

**Step 5: Fix `SubmitFromFrozen` raw components construction**

In the frozen buffer submit path (around line 624), where `RawComponentValue` is constructed from `CreatedComponent`:
```csharp
rawComponents[j] = new RawComponentValue(sc.ComponentType, frozen.Slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
```

The `sc.ComponentType` is no longer available. Need to get it from somewhere. This is in a loop over `_tempComponents[j]` which is `List<(int ComponentTypeId, CreatedComponent Component)>`. So the ComponentTypeId is available:
```csharp
var (typeId, sc) = sourceComponents[j];
rawComponents[j] = new RawComponentValue((ComponentType)typeId, frozen.Slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
```

**Step 6: Fix `BuildCreatedEntityComponentsForDelta` and `BuildCreatedEntityComponentsFromFrozen`**

Same pattern — these construct `RawComponentValue` from `CreatedComponent`. Need to pass ComponentType from the key.

For `BuildCreatedEntityComponentsForDelta`: the `ExtractAndSortComponents` already returns `ComponentType[] types`. Use `types[i]` instead of `sc.ComponentType`:
```csharp
for (var i = 0; i < count; i++)
{
    var sc = sources[i];
    rawComponents[i] = new RawComponentValue(types[i], _slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
}
```

For `BuildCreatedEntityComponentsFromFrozen`: same — use `types[i]`.

**Step 7: Build and run tests**

Run: `dotnet test tests/MiniArch.Tests -c Release --no-restore`
Expected: 307 pass, 0 fail.

**Step 8: Run perf gate**

Run: `dotnet run -c Release --project perf/HeroComing.Perf`
Expected: Movement ≥ 1027 rounds/s, Attack ≥ 647 rounds/s.

**Step 9: Commit**

```bash
git add src/MiniArch/Core/CommandBuffer.cs
git commit -m "perf: remove redundant ComponentType from CreatedComponent (16B→12B)"
```

---

### Task 3: Remove `ComponentTypeId` from `EntityOpSlot` + union `AddSetData`/`RemoveComponentType`

**Rationale:** `EntityOpSlot` stores `ComponentTypeId` (int) that duplicates the InlineMap key. `AddSetData` (16B) and `RemoveComponentType` (4B) are mutually exclusive by Kind. We can union them. Target: ~20B from ~28B.

**Strategy:** Use `[StructLayout(LayoutKind.Explicit)]` to overlay `RemoveComponentType` at the start of `AddSetData` (both start with `ComponentType`). Drop `ComponentTypeId` entirely.

**Files:**
- Modify: `src/MiniArch/Core/CommandBuffer.cs` — `EntityOpSlot`, `EmitOp`, `EmitOpFromFrozen`, `ApplyOpDirect`, `ApplyOpDirectFromFrozen`, all construction sites
- Test: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Rewrite `EntityOpSlot` with explicit layout**

```csharp
[StructLayout(LayoutKind.Explicit)]
private struct EntityOpSlot
{
    [FieldOffset(0)] public byte Kind;
    // 3 bytes padding
    [FieldOffset(4)] public ComponentType ComponentType; // shared by Add/Set and Remove
    [FieldOffset(8)] public int SlabIndex;
    [FieldOffset(12)] public int DataOffset;
    [FieldOffset(16)] public int DataSize;
}
```

Size: 20B. All fields are value types, no GC references → safe for explicit layout.

`AddSetEntry` struct is no longer needed — its fields are inlined into `EntityOpSlot`.

**Step 2: Remove `AddSetEntry` struct definition**

Delete:
```csharp
private struct AddSetEntry { ... }
```

**Step 3: Fix all `EntityOpSlot` construction sites**

- Add: `new EntityOpSlot { Kind = OpKindAdd, ComponentType = info.ComponentType, SlabIndex = slabIndex, DataOffset = offset, DataSize = info.Size }`
- Set: same with `Kind = OpKindSet`
- Remove: `new EntityOpSlot { Kind = OpKindRemove, ComponentType = info.ComponentType }`
  (SlabIndex, DataOffset, DataSize unused for Remove — leave default 0)

**Step 4: Fix `EmitOp`, `EmitOpFromFrozen`, `ApplyOpDirect`, `ApplyOpDirectFromFrozen`**

All read from `slot.AddSetData.XXX` → change to `slot.XXX`:

```csharp
// EmitOp
case OpKindAdd:
    delta.AddCommands.Add(new RawComponentCommand(entity, slot.ComponentType, slot.DataOffset, slot.DataSize, _slabs[slot.SlabIndex]));
    break;
case OpKindSet:
    delta.SetCommands.Add(new RawComponentCommand(entity, slot.ComponentType, slot.DataOffset, slot.DataSize, _slabs[slot.SlabIndex]));
    break;
case OpKindRemove:
    delta.RemoveCommands.Add(new RawRemoveCommand(entity, slot.ComponentType));
    break;
```

Same pattern for `EmitOpFromFrozen`, `ApplyOpDirect`, `ApplyOpDirectFromFrozen`.

**Step 5: Build and run tests**

Run: `dotnet test tests/MiniArch.Tests -c Release --no-restore`
Expected: 307 pass, 0 fail.

**Step 6: Run perf gate**

Run: `dotnet run -c Release --project perf/HeroComing.Perf`
Expected: Movement ≥ current baseline (1027), ideally higher. Attack ≥ current baseline (647).

**Step 7: Verify struct size with `Unsafe.SizeOf`**

Add a temporary assertion:
```csharp
System.Diagnostics.Debug.Assert(System.Runtime.CompilerServices.Unsafe.SizeOf<EntityOpSlot>() == 20);
```
Remove after verification.

**Step 8: Commit**

```bash
git add src/MiniArch/Core/CommandBuffer.cs
git commit -m "perf: shrink EntityOpSlot from ~28B to 20B via explicit layout union, drop redundant ComponentTypeId"
```

---

### Task 4: Update knowledge base

**Files:**
- Modify: `.knowledge/kb-command-buffer-feasibility.md`
- Modify: `.knowledge/kb-hero-pipeline-regression.md` (if baseline changed)

**Step 1: Update struct size table and perf numbers in knowledge base**

Add rows for `CreatedComponent` 16B→12B and `EntityOpSlot` 28B→20B to the existing struct shrink table. Update perf baseline if changed.

**Step 2: Run perf gate one final time to confirm baseline**

Run: `dotnet run -c Release --project perf/HeroComing.Perf`

**Step 3: Commit**

```bash
git add .knowledge/
git commit -m "docs: update CommandBuffer struct shrink phase 2 perf data"
```
