# Shared Value Tracker Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 将 ChangeTracker\<T\> 从 per-query 拥有改为 world 共享，使 ValueChanges\<T\>() 变为非消耗性只读操作，添加显式 ClearChanges\<T\>() API，消除重复 Set fanout 和弱引用生命周期复杂度。

**Architecture:** World 拥有 `SharedTrackerRegistry`，按组件类型持有唯一的 `ChangeTracker<T>`。多个同类型 query 共享同一个 tracker。`ValueChanges<T>()` 返回只读 span（不 drain、不 swap）。`ClearChanges<T>()` 显式清空 dirty 状态。`ApplyTypedSet` 热路径从遍历 `List<WeakReference<...>>` 改为直接索引共享 tracker（O(1)）。移除 weak-ref tracker fanout 和 `_singleTypedTracker` 缓存；`IChangeTrackerControl` 仅保留为 registry 的内部 type-erased 控制接口。

**Tech Stack:** C# unmanaged generics, `Unsafe.As`, `MemoryMarshal`, xUnit, double-buffered `TypedChange<T>[]`.

---

### Task 1: RED tests for non-draining read + explicit clear

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs` (add new test class or section)

**Step 1: Write the failing test — ValueChanges is non-destructive**

```csharp
[Fact]
public void ValueChanges_is_non_destructive_multiple_calls_return_same_data()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    var span1 = q.ValueChanges<Position>();
    var span2 = q.ValueChanges<Position>();

    Assert.Equal(span1.Length, span2.Length);
    Assert.Equal(1, span1.Length);
    Assert.Equal(span1[0].Entity, span2[0].Entity);
    Assert.Equal(span1[0].New.X, span2[0].New.X);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "ValueChanges_is_non_destructive" --nologo`
Expected: FAIL — current `Drain()` swaps buffers, second call returns empty

**Step 3: Write the failing test — explicit clear empties changes**

```csharp
[Fact]
public void Explicit_clear_empties_value_changes()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    Assert.Equal(1, q.ValueChanges<Position>().Length);

    q.ClearChanges<Position>();

    Assert.Equal(0, q.ValueChanges<Position>().Length);
}

[Fact]
public void ClearChanges_after_no_changes_is_noop()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    Assert.Equal(1, q.ValueChanges<Position>().Length);
    q.ClearChanges<Position>();
    Assert.Equal(0, q.ValueChanges<Position>().Length);
    q.ClearChanges<Position>();  // second clear, no changes present
    Assert.Equal(0, q.ValueChanges<Position>().Length);
}
```

**Step 4: Run test to verify it fails**

Run: `dotnet test -c Release --filter "Explicit_clear_empties" --nologo`
Expected: FAIL — `ClearChanges<T>()` doesn't exist yet

**Step 5: Write the failing test — World level ClearChanges**

```csharp
[Fact]
public void World_ClearChanges_clears_all_value_changes()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    Assert.Equal(1, q.ValueChanges<Position>().Length);

    world.ClearChanges<Position>();

    Assert.Equal(0, q.ValueChanges<Position>().Length);
}
```

**Step 6: Run test to verify it fails**

Run: `dotnet test -c Release --filter "World_ClearChanges_clears" --nologo`
Expected: FAIL — `World.ClearChanges<T>()` doesn't exist yet

**Step 7: Write failing test — ClearChanges also resets slot so next Set records fresh**

```csharp
[Fact]
public void After_clear_new_set_produces_fresh_entry()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    Assert.Equal(1, q.ValueChanges<Position>().Length);
    q.ClearChanges<Position>();

    world.Set(e, new Position(30, 40));

    var span = q.ValueChanges<Position>();
    Assert.Equal(1, span.Length);
    Assert.Equal(30, span[0].New.X);
}
```

**Step 8: Run test to verify it fails**

Run: `dotnet test -c Release --filter "After_clear_new_set" --nologo`
Expected: FAIL — current drain clears slot, new model must ensure clear + re-set works

---

### Task 2: RED tests for multiple same-type queries sharing semantics

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs`

**Step 1: Write the failing test — two queries share same tracker**

```csharp
[Fact]
public void Two_queries_same_component_type_see_identical_changes()
{
    using var world = new World();
    var q1 = world.Track().Capture<Position>().Previous();
    var q2 = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    var span1 = q1.ValueChanges<Position>();
    var span2 = q2.ValueChanges<Position>();

    Assert.Equal(span1.Length, span2.Length);
    Assert.Equal(1, span1.Length);
    Assert.Equal(span1[0].Entity, span2[0].Entity);
    Assert.Equal(span1[0].New.X, span2[0].New.X);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "Two_queries_same_component" --nologo`
Expected: May PASS on current code because duplicated per-query trackers happen to expose identical data. Keep this as behavior regression coverage; Task 8 perf enforces that same-type consumer count no longer increases Set cost.

**Step 3: Write the global-clear sharing verification test**

```csharp
[Fact]
public void ClearChanges_on_one_query_clears_shared_tracker_for_all_same_type_queries()
{
    using var world = new World();
    var q1 = world.Track().Capture<Position>().Previous();
    var q2 = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));

    world.Set(e, new Position(10, 20));
    Assert.Equal(1, q1.ValueChanges<Position>().Length);
    Assert.Equal(1, q2.ValueChanges<Position>().Length);

    q1.ClearChanges<Position>();

    Assert.Equal(0, q1.ValueChanges<Position>().Length);
    Assert.Equal(0, q2.ValueChanges<Position>().Length);
}
```

**Step 4: Run test to verify it fails**

Run: `dotnet test -c Release --filter "ClearChanges_on_one_query" --nologo`
Expected: FAIL — `ClearChanges<T>()` doesn't exist yet

**Step 5: Write failing test — Set records once even with multiple consumers**

```csharp
[Fact]
public void Set_records_once_per_component_type_regardless_of_consumer_count()
{
    using var world = new World();
    var q1 = world.Track().Capture<Position>().Previous();
    var q2 = world.Track().Capture<Position>().Previous();
    var q3 = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(0, 0));

    world.Set(e, new Position(10, 20));

    // All three see exactly one change
    Assert.Equal(1, q1.ValueChanges<Position>().Length);
    Assert.Equal(1, q2.ValueChanges<Position>().Length);
    Assert.Equal(1, q3.ValueChanges<Position>().Length);

    // Architecture-level single-record guarantee is verified by the perf harness:
    // increasing same-type consumer count must not linearly increase Set cost.
}
```

**Step 6: Run test to verify it fails**

Run: `dotnet test -c Release --filter "Set_records_once" --nologo`
Expected: PASS behaviorally today; keep as regression guard, and enforce the single-record guarantee in Task 8 perf.

---

### Task 3: RED tests for filter/second-capture still disabled

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs`

**Step 1: Write test — filter disables typed fast path even with shared tracker**

```csharp
[Fact]
public void Filter_disables_typed_fast_path_value_changes_empty()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().With<Position>().Previous();
    var e = world.Create(new Position(0, 0));
    world.Set(e, new Position(10, 20));

    // Filter present → typed fast path disabled → no value changes
    Assert.Equal(0, q.ValueChanges<Position>().Length);
}
```

**Step 2: Run test — this should already pass (current behavior)**

Run: `dotnet test -c Release --filter "Filter_disables_typed" --nologo`
Expected: PASS — existing behavior, this is a regression guard

**Step 3: Write test — second capture disables typed fast path**

```csharp
[Fact]
public void Second_capture_disables_typed_fast_path()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Capture<Velocity>().Previous();
    var e = world.Create(new Position(0, 0), new Velocity(1, 2));
    world.Set(e, new Position(10, 20));

    // Two captures → typed fast path disabled → ValueChanges<Position> empty
    Assert.Equal(0, q.ValueChanges<Position>().Length);
}
```

**Step 4: Run test**

Run: `dotnet test -c Release --filter "Second_capture_disables" --nologo`
Expected: PASS — existing behavior, regression guard

---

### Task 4: RED tests for RestoreState — no stale data and clear after destroy/id reuse

**Files:**
- Modify: `tests/MiniArch.Tests/Persistence/ChangeTrackingSnapshotTests.cs`

**Step 1: Write failing test — RestoreState clears all changes**

```csharp
[Fact]
public void RestoreState_clears_tracked_changes()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e = world.Create(new Position(1, 2));

    var snap = world.CaptureState();

    world.Set(e, new Position(10, 20));
    Assert.Equal(1, q.ValueChanges<Position>().Length);

    world.RestoreState(snap);
    // After restore, the query should self-heal and see no changes
    Assert.Equal(0, q.ValueChanges<Position>().Length);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "RestoreState_clears_tracked" --nologo`
Expected: FAIL — current RestoreState clears `_typedTrackers = null` but the query still has a dangling reference to the old tracker

**Step 3: Write failing test — destroy + id reuse doesn't leak stale data**

```csharp
[Fact]
public void Destroy_and_id_reuse_clears_old_change_entry()
{
    using var world = new World();
    var q = world.Track().Capture<Position>().Previous();
    var e1 = world.Create(new Position(1, 2));
    var oldId = e1.Id;

    world.Set(e1, new Position(10, 20));
    Assert.Equal(1, q.ValueChanges<Position>().Length);
    q.ClearChanges<Position>();

    world.Destroy(e1);
    // Reuse the same slot
    var e2 = world.Create(new Position(100, 200));
    Assert.Equal(oldId, e2.Id);  // same slot, different version

    world.Set(e2, new Position(300, 400));
    var span = q.ValueChanges<Position>();
    Assert.Equal(1, span.Length);
    Assert.Equal(300, span[0].New.X);   // new entity's change, not old
    Assert.Equal(Enitity(e2, 300, 400), span[0]); // verify correct entity
}
```

**Step 4: Run test to verify it fails — this may actually pass if ClearTypedTrackerSlots works correctly**

Run: `dotnet test -c Release --filter "Destroy_and_id_reuse" --nologo`
Expected: PASS or FAIL — current `ClearTypedTrackerSlots` on Destroy handles this. This test serves as a regression guard.

---

### Task 5: Implement `ChangeTracker<T>.Read()` / `Clear()` with stable span semantics

**Files:**
- Modify: `src/MiniArch/ChangeTracker.cs` — replace `Drain()` with `Read()` + `Clear()`

**Step 1: Change Drain to Read — non-destructive span view**

```csharp
// ChangeTracker.cs — replace Drain()
/// <summary>
/// Returns a read-only view of the current tick's changes.
/// Non-destructive: does not swap buffers or clear dirty flags.
/// The returned span is valid until the next Clear() call.
/// </summary>
internal ReadOnlySpan<TypedChange<T>> Read()
{
    var count = DirtyCount;
    if (count == 0) return ReadOnlySpan<TypedChange<T>>.Empty;
    return new ReadOnlySpan<TypedChange<T>>(ActiveLog, 0, count);
}
```

**Step 2: Add Clear() to replace the destructive part of Drain**

```csharp
/// <summary>
/// Clears all dirty state: swaps buffers, resets DirtyCount,
/// clears SlotByEntityPlusOne for all tracked entities.
/// After Clear(), Read() returns empty until the next Set.
/// </summary>
internal void Clear()
{
    var count = DirtyCount;
    if (count == 0) return;

    // Current write log becomes spare; spare becomes active (no data loss)
    var drained = ActiveLog;
    ActiveLog = SpareLog;
    SpareLog = drained;

    // Clear SlotByEntityPlusOne for drained entities
    for (var i = 0; i < count; i++)
        SlotByEntityPlusOne[drained[i].Entity.Id] = 0;

    DirtyCount = 0;
}
```

**Step 3: Keep Reset() for internal reset scenarios (RestoreState path)**

`Reset()` already exists and does the same as `Clear()`. Consider keeping both or aliasing.

**Step 4: Build**

Run: `dotnet build -c Release --nologo src/MiniArch`
Expected: success

---

### Task 6: Implement world-owned shared tracker registry and remove weak typed tracker fanout

**Files:**
- Create: `src/MiniArch/Core/SharedTrackerRegistry.cs` — new type
- Modify: `src/MiniArch/Core/World.cs` — replace `_typedTrackers` / `_singleTypedTracker` with registry
- Modify: `src/MiniArch/Core/World.StructuralChange.cs` — replace ApplyTypedSet typed tracker dispatch
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs` — update ClearTypedTrackerSlots
- Modify: `src/MiniArch/ChangeQuery.cs` — remove per-query tracker ownership, use world registry
- Keep/modify: `src/MiniArch/ChangeTracker.cs` internal `IChangeTrackerControl` — no longer weak-ref owned, still useful for type-erased clear/reset in the shared registry

**Step 1: Write tests first (Task 1, 2 already cover)**

Ensure all RED tests from Task 1, 2, 4 exist and are failing.

**Step 2: Implement SharedTrackerRegistry**

```csharp
// SharedTrackerRegistry.cs
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// World-owned registry of ChangeTracker&lt;T&gt; instances.
/// One tracker per component type; shared by all ChangeQuery consumers.
/// Uses Component&lt;T&gt;.ComponentType.Value as a dense index.
/// Non-thread-safe (matches World's threading model).
/// </summary>
internal sealed class SharedTrackerRegistry
{
    // Growable array indexed by componentType.Value.
    // null = no tracker for that type.
    private IChangeTrackerControl?[] _trackers = new IChangeTrackerControl?[32];

    // Expose the internal array for the ApplyTypedSet fast path
    internal IChangeTrackerControl?[] RawArray => _trackers;
    internal int RawLength => _trackers.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetTracker(int typeId, [MaybeNullWhen(false)] out IChangeTrackerControl tracker)
    {
        if ((uint)typeId < (uint)_trackers.Length)
        {
            tracker = _trackers[typeId];
            return tracker is not null;
        }
        tracker = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChangeTracker<T>? GetTracker<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        if ((uint)typeId < (uint)_trackers.Length)
            return _trackers[typeId] as ChangeTracker<T>;
        return null;
    }

    internal ChangeTracker<T> GetOrCreateTracker<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        if ((uint)typeId >= (uint)_trackers.Length)
            Array.Resize(ref _trackers, Math.Max(typeId + 1, _trackers.Length * 2));

        if (_trackers[typeId] is ChangeTracker<T> existing)
            return existing;

        var tracker = new ChangeTracker<T>();
        _trackers[typeId] = tracker;
        return tracker;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasTracker(int typeId)
    {
        return (uint)typeId < (uint)_trackers.Length && _trackers[typeId] is not null;
    }

    internal void Clear()
    {
        Array.Clear(_trackers, 0, _trackers.Length);
    }

    internal void ClearSlot(int entityId, int typeId)
    {
        if ((uint)typeId < (uint)_trackers.Length && _trackers[typeId] is { } tracker)
            tracker.ClearSlot(entityId);
    }

    internal void ClearAllSlots(int entityId)
    {
        for (var i = 0; i < _trackers.Length; i++)
            _trackers[i]?.ClearSlot(entityId);
    }

}

// Extend IChangeTrackerControl if needed (add DirtyCount property)
internal interface IChangeTrackerControl
{
    ComponentType ComponentType { get; }
    int DirtyCount { get; }  // NEW
    void ClearSlot(int entityId);
}
```

**Step 3: Update ChangeTracker to implement DirtyCount**

```csharp
// ChangeTracker<T> — add to IChangeTrackerControl implementation
int IChangeTrackerControl.DirtyCount => DirtyCount;
```

**Step 4: Update World.cs — replace typed tracker fields**

```csharp
// World.cs — replace fields:
// OLD:
// internal List<WeakReference<IChangeTrackerControl>>? _typedTrackers;
// internal WeakReference<IChangeTrackerControl>? _singleTypedTracker;
// NEW:
internal SharedTrackerRegistry SharedTrackers = new SharedTrackerRegistry();

// Replace AddTypedTracker, RemoveTypedTracker, UpdateTypedTrackerFastPath,
// PruneDeadTypedTrackers — all deleted.

// Update ClearTypedTrackerSlots:
internal void ClearTypedTrackerSlots(int entityId, ComponentType? componentType = null)
{
    if (componentType is not null)
        SharedTrackers.ClearSlot(entityId, componentType.Value);
    else
        SharedTrackers.ClearAllSlots(entityId);
}

// Update RestoreState:
// OLD:
// _typedTrackers = null;
// _singleTypedTracker = null;
// NEW:
SharedTrackers.Clear();

// Update Dispose:
// OLD:
// _typedTrackers = null;
// _singleTypedTracker = null;
// NEW:
SharedTrackers.Clear();
```

**Step 5: Update ChangeQuery.cs — use world's shared tracker**

```csharp
// ChangeQuery.cs — remove _typedTracker field entirely.
// Remove DeactivateTypedTracker(), _typedTracker, IChangeTrackerControl reference.
// Replace Typed fast path with world lookup:

private ChangeTracker<T>? GetSharedTracker<T>() where T : unmanaged
{
    return _world.SharedTrackers.GetTracker<T>();
}

// Update ValueChanges<T>():
public ReadOnlySpan<TypedChange<T>> ValueChanges<T>() where T : unmanaged
{
    EnsureUsable();
    _consumed = true;
    if (!_hasPrevious || _hasFilter || _capturedTypes.Count != 1)
        return ReadOnlySpan<TypedChange<T>>.Empty;
    var tracker = GetSharedTracker<T>();
    if (tracker is null)
        return ReadOnlySpan<TypedChange<T>>.Empty;
    return tracker.Read();
}

// Add ClearChanges<T>():
public void ClearChanges<T>() where T : unmanaged
{
    EnsureUsable();
    var tracker = GetSharedTracker<T>();
    tracker?.Clear();
}

// Remove ActivateTypedTrackerForCapturedType, TryActivateTypedTracker,
// DeactivateTypedTracker, RefreshTypedTrackerActivation, _typedTracker field.

// TryActivateTypedTracker becomes GetOrCreateSharedTracker:
private void EnsureTrackerCreated<T>() where T : unmanaged
{
    _world.SharedTrackers.GetOrCreateTracker<T>();
}

// Update Capture<T>() — simplified:
public ChangeQuery Capture<T>() where T : unmanaged
{
    EnsureUsable();
    if (_consumed)
        throw new InvalidOperationException("Cannot Capture after enumeration has started.");
    var ct = Component<T>.ComponentType;
    if (_capturedTypes.Contains(ct)) return this;
    _capturedTypes.Add(ct);

    if (_hasPrevious && !_hasFilter && _capturedTypes.Count == 1)
        EnsureTrackerCreated<T>();
    // else: typed fast path disabled, no tracker needed
    // (existing behavior: multi-capture / filter → no typed value changes)

    RefreshTransitionRegistration();
    return this;
}

// Update Previous():
public ChangeQuery Previous()
{
    EnsureUsable();
    if (_consumed)
        throw new InvalidOperationException("Cannot enable Previous after enumeration has started.");
    _hasPrevious = true;

    if (!_hasFilter && _capturedTypes.Count == 1)
        EnsureTrackerCreatedForCapturedType();

    return this;
}

private void EnsureTrackerCreatedForCapturedType()
{
    if (!_hasPrevious || _capturedTypes.Count != 1 || _hasFilter) return;
    var ct = _capturedTypes[0];
    // Use reflection to call EnsureTrackerCreated<T> with the right T
    var runtimeType = ComponentRegistry.Shared.GetType(ct);
    if (runtimeType is null) return;
    var method = typeof(ChangeQuery).GetMethod("EnsureTrackerCreated",
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    method.MakeGenericMethod(runtimeType).Invoke(this, null);
}
```

Alternative: avoid reflection by having `SharedTrackerRegistry.GetOrCreateForType(ComponentType)` that uses the same generic dispatch mechanism.

**Step 6: Update World.StructuralChange.cs — replace ApplyTypedSet dispatch**

```csharp
// ApplyTypedSet<T> — simplified typed tracker path:
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal static void ApplyTypedSet<T>(Entity entity, EntityRecord info, ComponentType componentType, in T component) where T : unmanaged
{
    var archetype = info.Archetype!;

    if (!archetype.TryGetComponentIndex(componentType, out var componentIndex))
    {
        throw new InvalidOperationException(
            $"Entity {entity} does not have component {typeof(T).Name}.");
    }

    var world = archetype._owner;
    if (world is not null)
    {
        ref var cell = ref archetype.GetComponentRefAt<T>(componentIndex, info.RowIndex);
        var id = entity.Id;

        // NEW: Direct tracker lookup via registry
        var tracker = world.SharedTrackers.GetTracker<T>();
        if (tracker is not null)
            RecordTypedChange(tracker, entity, id, ref cell, in component);

        // Write component value
        cell = component;
        return;
    }

    archetype.SetComponentAtTyped(componentIndex, info.RowIndex, in component);
}
```

**Step 7: Update CommandStreamCore.cs — replace _typedTrackers check**

```csharp
// CommandStreamCore.cs — replace both occurrences:
// OLD: if (world._typedTrackers is not null)
// NEW: if (world.SharedTrackers.HasAny())
//   or equivalently — just call ApplyTypedSet which now does the check internally

// Simplest: always call ApplyTypedSet (it does a lightweight GetTracker check).
// Remove the _typedTrackers guard entirely — ApplyTypedSet handles both cases.
```

**Step 8: Update World.cs — remove old tracker methods**

Delete: `AddTypedTracker`, `RemoveTypedTracker`, `PruneDeadTypedTrackers`, `UpdateTypedTrackerFastPath`, `_typedTrackers`, `_singleTypedTracker`. Keep `ClearTypedTrackerSlots` as a small registry delegator.

**Step 9: Update World.EntityLifecycle.cs — ClearTypedTrackerSlots still works (redirected to registry)**

No changes needed if we updated `ClearTypedTrackerSlots` in World.cs to delegate to registry.

**Step 10: Build + Run tests**

Run: `dotnet build -c Release --nologo`
Expected: success

Run: `dotnet test -c Release --no-build --nologo --filter "ValueChanges|ClearChanges|shared_tracker|SharedTracker"`
Expected: All previously RED tests turn GREEN

---

### Task 7: Update World.ApplyTypedSet and CommandStreamCore tracking gate

**Files:**
- Modify: `src/MiniArch/Core/World.StructuralChange.cs` — already done in Task 6
- Modify: `src/MiniArch/Core/CommandStreamCore.cs` — optimize tracking gate

**Step 1: CommandStreamCore.Set fast path — always call ApplyTypedSet**

Current code has two branches in the Set-only fast path (lines 2711-2749):
- If `_typedTrackers is not null`: call `ApplyTypedSet`
- Else: fast path with `SetComponentAtFlatNoTrack` / `SetComponentAtTypedNoTrack`

With shared registry, `ApplyTypedSet` already does a lightweight `GetTracker<T>` check. The overhead of calling `ApplyTypedSet` vs the no-track fast path is:
- One extra `TryGetComponentIndex` (but the fast path also does this)
- One extra `GetTracker<T>` (array read + null check)
- One extra ref read + write (same cost)

So we can just **always** call `ApplyTypedSet` from CommandStream and delete the `*NoTrack` branches. But this changes performance characteristics (the no-track fast path was intentionally avoiding the `is null` checks and ref reads).

**Decision: Keep the fast path, just adjust the guard.**

```csharp
// CommandStreamCore — Set-only fast path:
if (world.SharedTrackers.HasTracker(compType.Value))
{
    // Tracking active: use ApplyTypedSet which handles tracker recording
    for (var i = 0; i < count; i++)
    {
        ref var entry = ref Unsafe.Add(ref entriesRef, i);
        var record = world.GetRecordFast(entry.Entity);
        World.ApplyTypedSet(entry.Entity, record, compType, in entry.Value);
    }
    return;
}

// No tracking for this type: fast path (unchanged)
// ... existing no-track code ...
```

**Step 2: Run tracking + not-tracking perf tests**

Run: `dotnet test -c Release --nologo` — all tests pass

---

### Task 8: Add/extend perf harness for track-specific scenarios

**Files:**
- Modify: `tools/perf/HeroComing.Perf/HeroPipelineBenchmark.cs`
- Modify: `tools/perf/GameTickSim.Perf/Program.cs` (if applicable)

**Step 1: Add reuse count dimorphism to HeroComing.Perf**

In `HeroPipelineBenchmark.cs`, add a test scenario that runs with `Capture<Position>().Previous()` active on 1, 2, 8 same-type queries:

```csharp
// HeroPipelineBenchmark.cs — near existing Setup method
[Params(1, 2, 8)]
public int TrackerConsumerCount { get; set; }

private List<ChangeQuery> _trackingQueries = new();

private void SetupTrackingQueries()
{
    _trackingQueries.Clear();
    for (var i = 0; i < TrackerConsumerCount; i++)
    {
        _trackingQueries.Add(World.Track().Capture<Position>().Previous());
    }
}

// In Tick loop: consume ValueChanges from all queries (simulate rendering use)
```

**Step 2: Add density variations to GameTickSim.Perf**

Extend the existing GameTickSim scenarios with Tracking scenarios at different densities (1%, 10%, 50%, 100%) using the new shared tracker model, and measure:
- Throughput with N=1, N=2, N=8 consumers
- Allocation count
- Difference between ValueChanges-only vs ValueChanges + ClearChanges per tick

**Step 3: Add explicit clear measurement**

Add a benchmark step that measures `ClearChanges<T>()` cost at different DirtyCount sizes (10, 100, 1000, 10000) to validate O(dirtyCount) expectation.

**Step 4: Run verification**

Run: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
Expected: no regression (tracking off) — Movement ≥ 1642, Attack ≥ 997

Run: `dotnet run -c Release --project tools/perf/GameTickSim.Perf`
Expected: collect data for knowledge base update

---

### Task 9: Run full regression suite

**Step 1: Full test suite**

Run: `dotnet test -c Release --nologo`
Expected: all tests pass (including new shared tracker tests)

**Step 2: Soak test**

Run: `dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 16 --frames 50000 --quiet`
Expected: 16/16 PASS

**Step 3: Check soak with tracking active**

Run: `dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 16 --frames 50000 --track-observer --quiet`
Expected: 16/16 PASS (no divergence from tracking activity)

**Step 4: HeroComing gate**

Run: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
Expected: Movement ≥ 1642, Attack ≥ 997, memory stable

**Step 5: Lockstep soak (multi-host)**

Run: `dotnet run -c Release --project tools/soak/MiniArch.Soak.Lockstep -- --sweep 8 --frames 5000 --quiet`
Expected: 8/8 PASS (tracking is ephemeral, but RestoreState path must not break determinism)

---

### Task 10: Update .knowledge/kb-change-tracking.md and INDEX

**Files:**
- Modify: `.knowledge/kb-change-tracking.md` — update Architecture, API, decision sections
- Modify: `.knowledge/INDEX.md` — if module map changes

**Step 1: Update kb-change-tracking.md sections**

Key changes:
- Architecture: tracker ownership changes (World owns, query shares)
- API: `ValueChanges<T>()` non-destructive, `ClearChanges<T>()` added, `World.ClearChanges<T>()` added
- Decision: new decisions for shared tracker model (rationale for O(1) per-type, why global clear not per-query, why remove weak ref pattern)
- Data flow: `ApplyTypedSet` simplified path
- Performance: expected scaling (Set cost constant with consumer count)
- Entry points: `ChangeTracker.cs` simplified, `SharedTrackerRegistry.cs` added, `IChangeTrackerControl` retained only as an internal type-erased control interface
- Remove: `_singleTypedTracker` / `_typedTrackers` / `WeakReference<IChangeTrackerControl>` references
- Remove: IChangeTrackerControl interface coverage (if deleted)

**Step 2: Update INDEX.md if needed**

Verify the INDEX.md module map is still accurate. Add `kb-shared-tracker-registry.md` if a new knowledge page is warranted. (Likely just update kb-change-tracking.md — single page is sufficient.)

**Step 3: Update kb-changelog.md**

Add entry: `2026-07-08 shared per-component value tracker — World owns, query shares; non-draining ValueChanges + explicit ClearChanges`

---

### Verification (final)

After all tasks:

1. `dotnet build -c Release` → clean
2. `dotnet test -c Release --nologo` → all tests pass (including new shared tracker tests)
3. `dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 16 --frames 50000 --quiet` → 16/16 PASS
4. `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` → no regression
