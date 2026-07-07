# Change Tracking Unification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace `ChangeQuery<T>` (per-type, generic) with `ChangeQuery` (multi-Capture, no generic). Add entity-centric `Changes()` with old/new snapshots. Add `Previous()` as per-query switch. Move `ModifiedChunks<T>()` to method-level generic.

**Architecture:** ChangeQuery loses its `<T>` generic. `.Capture<T>()` replaces implicit With<T> and registers component for tracking. Internal `Handler<T>` per captured type implements `IValueChangeSink<T>` for per-type state. World gets two new pre-hooks (`OnBeforeWrite`, `OnBeforeTransition`). `EntityChange`/`EntitySnapshot` are new public types for the entity-centric view.

**Tech Stack:** C# unmanaged generics, `IChangeQuery` interface, `IValueChangeSink<T>`, `List<WeakReference<IChangeQuery>>` dispatch pattern.

---

### Task 1: New public types — EntityChange + EntitySnapshot

**Files:**
- Create: `src/MiniArch/EntityChange.cs`

**Step 1: Write EntityChange and EntitySnapshot structs**

```csharp
// EntityChange.cs
using System;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

public readonly struct EntityChange
{
    public readonly Entity Entity;
    internal readonly byte[] Data;
    internal readonly int OldOffset;
    internal readonly int NewOffset;
    internal readonly int SnapshotSize;
    internal readonly int[] Offsets;     // precomputed per-type offset into snapshot
    internal readonly ComponentType[] Types;  // ordered captured types

    internal EntityChange(Entity entity, byte[] data, int oldOffset, int newOffset,
        int snapshotSize, int[] offsets, ComponentType[] types)
    {
        Entity = entity;
        Data = data;
        OldOffset = oldOffset;
        NewOffset = newOffset;
        SnapshotSize = snapshotSize;
        Offsets = offsets;
        Types = types;
    }

    public EntitySnapshot Old => new(Data, OldOffset, Offsets, Types);
    public EntitySnapshot New => new(Data, NewOffset, Offsets, Types);
}

public readonly ref struct EntitySnapshot
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly int[] _offsets;
    private readonly ComponentType[] _types;

    internal EntitySnapshot(byte[] data, int offset, int[] offsets, ComponentType[] types)
    {
        _data = data;
        _offset = offset;
        _offsets = offsets;
        _types = types;
    }

    public bool Has<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        for (var i = 0; i < _types.Length; i++)
            if (_types[i].Value == typeId) return true;
        return false;
    }

    public ref readonly T Get<T>() where T : unmanaged
    {
        var typeId = Component<T>.ComponentType.Value;
        for (var i = 0; i < _types.Length; i++)
        {
            if (_types[i].Value == typeId)
            {
                var off = _offset + _offsets[i];
                return ref Unsafe.As<byte, T>(ref _data[off]);
            }
        }
        throw new InvalidOperationException(
            $"Component {typeof(T).Name} was not captured in this change.");
    }
}
```

**Step 2: Build and verify it compiles**

Run: `dotnet build -c Release --nologo src/MiniArch`
Expected: success

**Step 3: Add a minimal test**

Wait — this type needs the rest of the system to be functional for integration tests. Unit tests for EntityChange/EntitySnapshot are mostly structural. Skip dedicated unit tests for now (they'll be tested via Changes() integration tests).

**Step 4: Commit**

```bash
git add src/MiniArch/EntityChange.cs
git commit -m "feat: EntityChange + EntitySnapshot public types"
```

---

### Task 2: World pre-hooks

**Files:**
- Modify: `src/MiniArch/Core/IChangeQuery.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/World.StructuralChange.cs`

**Step 1: Extend IChangeQuery with two new virtual methods**

```csharp
// IChangeQuery.cs
internal interface IChangeQuery
{
    void OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype);

    /// <summary>
    /// Called before a component value is written (Set). 
    /// Default no-op for backward compat. Entity is in 'archetype' at 'row'.
    /// </summary>
    void OnBeforeWrite(Entity entity, Archetype archetype, int row) { }

    /// <summary>
    /// Called before a structural change moves an entity out of 'archetype' at 'row'.
    /// Entity has NOT been moved yet — safe to read component values from (archetype, row).
    /// </summary>
    void OnBeforeTransition(Entity entity, Archetype archetype, int row) { }
}
```

C# 8+ supports default interface methods — no need to update existing implementations.

**Step 2: Add DispatchBeforeWrite to World**

```csharp
// World.cs, near AppendTransition (~line 239)
internal void DispatchBeforeWrite(Entity entity, Archetype archetype, int row)
{
    for (var i = _changeQueries.Count - 1; i >= 0; i--)
    {
        if (_changeQueries[i].TryGetTarget(out var query))
            query.OnBeforeWrite(entity, archetype, row);
        else
        {
            _changeQueries[i] = _changeQueries[_changeQueries.Count - 1];
            _changeQueries.RemoveAt(_changeQueries.Count - 1);
        }
    }
}

internal void DispatchBeforeTransition(Entity entity, Archetype archetype, int row)
{
    for (var i = _changeQueries.Count - 1; i >= 0; i--)
    {
        if (_changeQueries[i].TryGetTarget(out var query))
            query.OnBeforeTransition(entity, archetype, row);
        else
        {
            _changeQueries[i] = _changeQueries[_changeQueries.Count - 1];
            _changeQueries.RemoveAt(_changeQueries.Count - 1);
        }
    }
}
```

**Step 3: Add pre-hook calls to structural change paths**

In `World.StructuralChange.cs`:

```csharp
// ApplyTypedAdd — line ~118, before MoveEntityCore
internal void ApplyTypedAdd<T>(Entity entity, EntityRecord info, ComponentType componentType, in T component)
{
    var archetype = info.Archetype!;
    // ... TryGetComponentIndex check ...
    // ... GetOrCreateAddDestinationArchetype ...

    var sourceArchetype = info.Archetype!;
    
    // [NEW] pre-hook
    if (_anyTrackingActive) DispatchBeforeTransition(entity, sourceArchetype, info.RowIndex);
    
    var rowIdx = MoveEntityCore(entity, info, destination!);
    // ... rest unchanged ...
    
    FinishMoveEntity(entity, info, destination!, rowIdx);
    if (_anyTrackingActive) AppendTransition(entity, sourceArchetype, destination!);
}
```

In `RemoveBoxed` — same pattern before `MoveEntity`:

```csharp
// line ~264
if (_anyTrackingActive) DispatchBeforeTransition(entity, archetype, info.RowIndex);
MoveEntity(entity, info, destination!);
```

In `PlaceEntityInArchetype` (Create path — ~line 1178): No pre-hook needed (Old = null, entity didn't exist).

In `ApplyRawAdd` (MoveEntityFromBytes) — same as ApplyTypedAdd.

**Step 4: Add pre-hook to ApplyTypedSet / ApplyRawSet**

In `ApplyTypedSet<T>` — before the component write:

```csharp
// Around line 150, before SetComponentAtTyped
if (world is not null)
    world.DispatchBeforeWrite(entity, archetype, info.RowIndex);
```

Note: `ApplyTypedSet` is a static method. `world` is null only in reconstruction/destruction paths where entity is being torn down.

In `ApplyRawSet` — same pattern:

```csharp
// Around line ~190, before WriteComponentRaw
if (world is not null)
    world.DispatchBeforeWrite(entity, archetype, info.RowIndex);
```

**Step 5: Build + run existing tests (no regressions expected since all OnBeforeWrite/OnBeforeTransition are no-ops)**

Run: `dotnet build -c Release --nologo` + `dotnet test -c Release --no-build --nologo`
Expected: 807+ pass (same as before)

**Step 6: Commit**

```bash
git add src/MiniArch/Core/IChangeQuery.cs src/MiniArch/Core/World.cs src/MiniArch/Core/World.StructuralChange.cs
git commit -m "feat: World pre-hooks for change-tracking (OnBeforeWrite/OnBeforeTransition)"
```

---

### Task 3: New ChangeQuery class (no generic)

**Files:**
- Modify: `src/MiniArch/ChangeQuery.cs` — rewrite as non-generic
- Modify: `src/MiniArch/Core/World.cs` — add `Track()` entry point

**Step 1: Rewrite ChangeQuery.cs**

Replace the generic class with:

```csharp
using System.Collections.Generic;
using MiniArch.Core;

namespace MiniArch;

public sealed class ChangeQuery : IChangeQuery
{
    private readonly World _world;
    private QueryDescription _filter = new();
    private readonly List<Transition> _transitions = new(256);
    private readonly List<EntityChange> _entryResults = new(); // pooled output
    private bool _hasPrevious;
    private bool _consumed;

    // ── Captured type state ──
    private readonly List<ComponentType> _capturedTypes = new();
    private int[] _offsets = [];    // precomputed byte offsets per captured type
    private int _snapshotSize;      // total byte size of one snapshot (sum of sizeof(captured types))
    private QueryCache? _cache;

    // Per-type cursor for ModifiedChunks<T>
    private readonly Dictionary<int, long> _valueCursors = new();

    // Reusable snapshot write buffer (grown, never shrunk)
    private byte[] _snapBuffer = new byte[1024];
    private int _snapCount;    // number of entries in _snapBuffer
    // Each entry: [Old: captured types] [New: captured types] = 2 * _snapshotSize bytes

    internal ChangeQuery(World world)
    {
        _world = world;
    }

    public ChangeQuery Capture<T>() where T : unmanaged
    {
        if (_consumed)
            throw new InvalidOperationException("Cannot Capture after enumeration has started.");
        var ct = Component<T>.ComponentType;
        if (_capturedTypes.Contains(ct)) return this;
        _capturedTypes.Add(ct);
        _world.ActivateTracking(ct);

        // Rebuild offset table
        _offsets = new int[_capturedTypes.Count];
        var off = 0;
        for (var i = 0; i < _capturedTypes.Count; i++)
        {
            _offsets[i] = off;
            off += _capturedTypes[i].Size;
        }
        _snapshotSize = off;

        // Init value cursor for ModifiedChunks
        if (!_valueCursors.ContainsKey(ct.Value))
            _valueCursors[ct.Value] = 0;

        return this;
    }

    public ChangeQuery Previous()
    {
        if (_consumed)
            throw new InvalidOperationException("Cannot enable Previous after enumeration has started.");
        _hasPrevious = true;
        return this;
    }

    public ChangeQuery With<TU>() where TU : unmanaged
    {
        if (_consumed) throw ...;
        _filter = _filter.With<TU>();
        _cache = null;
        return this;
    }

    public ChangeQuery Without<TU>() where TU : unmanaged
    {
        if (_consumed) throw ...;
        _filter = _filter.Without<TU>();
        _cache = null;
        return this;
    }

    public ChangeQuery WithAny<TU>() where TU : unmanaged
    {
        if (_consumed) throw ...;
        _filter = _filter.WithAny<TU>();
        _cache = null;
        return this;
    }

    public IEnumerable<Transition> Transitions()
    {
        _consumed = true;
        var result = _transitions.ToArray();
        _transitions.Clear();
        return result;
    }

    public IEnumerable<ChunkView> ModifiedChunks<T>() where T : unmanaged
    {
        _consumed = true;
        var ct = Component<T>.ComponentType;
        if (!_capturedTypes.Contains(ct))
            throw new InvalidOperationException(
                $"Component {typeof(T).Name} was not captured. Call .Capture<{typeof(T).Name}>() first.");

        var query = _world.Query(_filter);
        var snapshotEpoch = _world.CurrentWriteEpoch;
        var cursor = _valueCursors[ct.Value];
        var result = new List<ChunkView>();
        var chunks = query.GetChunks().ToArray();
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            var chunk = chunks[ci];
            var arch = chunk.Archetype;
            if (!arch.TryGetComponentIndex(ct, out var col))
                continue;
            var versions = arch._columnVersions;
            if (versions is not null && versions[col] > cursor)
                result.Add(chunk);
        }
        _valueCursors[ct.Value] = snapshotEpoch;
        return result;
    }

    public EntityChange[] Changes()
    {
        _consumed = true;
        if (!_hasPrevious || _snapCount == 0)
            return [];

        var entryStride = _snapshotSize * 2;
        var totalSize = _snapCount * entryStride;
        var data = new byte[totalSize];
        Buffer.BlockCopy(_snapBuffer, 0, data, 0, totalSize);

        // Build EntityChange[] — entries were recorded sequentially
        // For now: just header info. Need to also store entity list.
        // [This will be expanded in Task 4 when pre-hooks populate properly]
        var result = new EntityChange[_snapCount];
        for (var i = 0; i < _snapCount; i++)
        {
            var off = i * entryStride;
            result[i] = new EntityChange(
                default, data, off, off + _snapshotSize,
                _snapshotSize, _offsets,
                _capturedTypes.ToArray());
        }

        _snapCount = 0;
        return result;
    }

    // ── IChangeQuery ──

    void IChangeQuery.OnTransition(Entity entity, Archetype? oldArchetype, Archetype? newArchetype)
    {
        _consumed = true;
        var cache = _cache ??= _world.Query(_filter).Advanced;
        var oldMatch = oldArchetype is { } o && cache.Matches(o);
        var newMatch = newArchetype is { } n && cache.Matches(n);
        if (!oldMatch && newMatch)
        {
            var cause = oldArchetype is null ? TransitionCause.Created : TransitionCause.Added;
            _transitions.Add(new Transition(cause, entity));
            // If Previous: capture snapshots here
            // Old from oldArchetype (or empty for Created)
            // New from newArchetype
            if (_hasPrevious) CaptureTransition(entity, oldArchetype, newArchetype);
        }
        else if (oldMatch && !newMatch)
        {
            var cause = newArchetype is null ? TransitionCause.Destroyed : TransitionCause.Removed;
            _transitions.Add(new Transition(cause, entity));
            if (_hasPrevious) CaptureTransition(entity, oldArchetype, newArchetype);
        }
    }

    private void CaptureTransition(Entity entity, Archetype? oldArch, Archetype? newArch)
    {
        // Read old values from oldArch
        if (oldArch is not null)
        {
            // Need row — not available here. This is where OnBeforeTransition comes in.
            // For now: signal that transition capture needs row info.
        }
        // Read new values from newArch
        // Need new row — from entity record
    }
}
```

This is a skeletal first pass — OnBeforeWrite hook will populate the actual snapshots. The key point is the type structure.

**Step 2: Add `World.Track()` entry point**

```csharp
// World.cs
public ChangeQuery Track()
{
    var query = new ChangeQuery(this);
    RegisterChangeQuery(query);
    return query;
}
```

Also add `internal long CurrentWriteEpoch => _writeEpoch;` if not already exposed.

**Step 3: Build**

Run: `dotnet build -c Release --nologo`
Expect: success (existing ChangeQuery<T> still there, no code references new ChangeQuery yet)

**Step 4: Commit**

```bash
git add src/MiniArch/ChangeQuery.cs src/MiniArch/Core/World.cs
git commit -m "feat: new non-generic ChangeQuery with Capture/Previous/ModifiedChunks<T>"
```

---

### Task 4: EntityChange snapshot data flow — pre-hook wiring

**Files:**
- Modify: `src/MiniArch/ChangeQuery.cs` — implement `IChangeQuery.OnBeforeWrite` and `OnBeforeTransition`

**Step 1: Implement OnBeforeWrite**

```csharp
void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row)
{
    if (!_hasPrevious || _capturedTypes.Count == 0) return;

    // Ensure buffer capacity
    var entryBytes = _snapshotSize * 2;
    var needed = (_snapCount + 1) * entryBytes;
    if (needed > _snapBuffer.Length)
        Array.Resize(ref _snapBuffer, Math.Max(needed, _snapBuffer.Length * 2));

    // Write Old snapshot (current values before write)
    var oldOff = _snapCount * entryBytes;
    for (var i = 0; i < _capturedTypes.Count; i++)
    {
        var ct = _capturedTypes[i];
        var colIdx = archetype.GetComponentIndexFast(ct);
        var src = archetype.GetComponentBytes(colIdx, row);
        Buffer.BlockCopy(src, 0, _snapBuffer, oldOff + _offsets[i], ct.Size);
    }

    // New snapshot will be written after the Set completes.
    // [We need a post-write hook or read after write completes]
    // For now: store entity for post-write processing
}
```

The challenge: we need both a pre-hook and post-hook, because Old is read before write and New is read after. Currently we only have `OnBeforeWrite`. We need `OnAfterWrite` too.

**Step 2: Add OnAfterWrite to IChangeQuery**

```csharp
void OnAfterWrite(Entity entity, Archetype archetype, int row) { }
```

**Step 3: Dispatch OnAfterWrite in ApplyTypedSet/ApplyRawSet after the write**

Same location, after `SetComponentAtTyped` / `WriteComponentRaw`.

**Step 4: Complete OnBeforeWrite + OnAfterWrite in ChangeQuery**

```csharp
// Per-entry temp storage: entity for each snapshot index
private readonly List<Entity> _snapEntities = new();

void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row)
{
    if (!_hasPrevious || _capturedTypes.Count == 0) return;

    var entryBytes = _snapshotSize * 2;
    var needed = (_snapCount + 1) * entryBytes;
    if (needed > _snapBuffer.Length)
        Array.Resize(ref _snapBuffer, Math.Max(needed, _snapBuffer.Length * 2));

    // Write Old
    var oldOff = _snapCount * entryBytes;
    for (var i = 0; i < _capturedTypes.Count; i++)
    {
        var ct = _capturedTypes[i];
        var colIdx = archetype.GetComponentIndexFast(ct);
        var src = archetype.GetComponentBytes(colIdx, row);
        Buffer.BlockCopy(src, 0, _snapBuffer, oldOff + _offsets[i], ct.Size);
    }

    _snapEntities.Add(entity);
    // New will be written in OnAfterWrite
}

void IChangeQuery.OnAfterWrite(Entity entity, Archetype archetype, int row)
{
    if (!_hasPrevious || _capturedTypes.Count == 0) return;

    var entryBytes = _snapshotSize * 2;
    // Find the matching entry (entity, last one)
    // We can track a counter instead
}
```

Simplify: use a counter instead of searching matched entities.

```csharp
private int _pendingOldSnapCount;  // how many Old snapshots have been recorded (no matching New yet)

void IChangeQuery.OnBeforeWrite(Entity entity, Archetype archetype, int row)
{
    // ... write Old at _snapCount * entryBytes ...
    _snapEntities.Add(entity);
    // Increment _snapCount AFTER Old so New also uses same index
}

void IChangeQuery.OnAfterWrite(Entity entity, Archetype archetype, int row)
{
    if (!_hasPrevious || _capturedTypes.Count == 0) return;
    // Must match the most recent OnBeforeWrite for the same entity
    var entryBytes = _snapshotSize * 2;
    var newOff = _snapCount * entryBytes + _snapshotSize; // New follows Old
    for (var i = 0; i < _capturedTypes.Count; i++)
    {
        var ct = _capturedTypes[i];
        var colIdx = archetype.GetComponentIndexFast(ct);
        var src = archetype.GetComponentBytes(colIdx, row);
        Buffer.BlockCopy(src, 0, _snapBuffer, newOff + _offsets[i], ct.Size);
    }
    _snapCount++;
}
```

Wait — this has a problem: `_snapCount` is incremented in `OnAfterWrite`, but `OnBeforeWrite` uses `_snapCount` to calculate the offset. So:

1. OnBeforeWrite: offset = `_snapCount * entryBytes` — writes Old there
2. OnAfterWrite: offset = `_snapCount * entryBytes + _snapshotSize` — writes New there
3. _snapCount++

This works because _snapCount hasn't changed between pre and post. The entry is at index `_snapCount` in both cases.

**Step 5: OnBeforeTransition implementation**

```csharp
// Store pre-move Old values
// These need to be paired with OnTransition post-move New values

void IChangeQuery.OnBeforeTransition(Entity entity, Archetype archetype, int row)
{
    if (!_hasPrevious || _capturedTypes.Count == 0) return;
    // Same as OnBeforeWrite: capture Old at current _snapCount
    // ... write Old ...
}

void IChangeQuery.OnTransition(Entity entity, Archetype? old, Archetype? @new)
{
    // ... existing transition logic ...
    if (_hasPrevious)
    {
        // Read New from the entity's current archetype (after move)
        var record = _world.GetRecordFast(entity);
        // ... write New at current _snapCount ...
        _snapCount++;
    }
}
```

**Step 6: Verify with existing tests**

Run: `dotnet build -c Release --nologo` + `dotnet test -c Release --no-build --nologo`
Expect: all existing tests pass (no regressions — new ChangeQuery not wired into tests yet)

**Step 7: Commit**

```bash
git add src/MiniArch/ChangeQuery.cs src/MiniArch/Core/IChangeQuery.cs src/MiniArch/Core/World.cs src/MiniArch/Core/World.StructuralChange.cs
git commit -m "feat: pre/post write hooks + transition snapshot capture"
```

---

### Task 5: Update ValueChangeBucket — pre-hook integration

The existing `ValueChangeBucket<T>` dispatches `OnValueChange` AFTER the write (post-hook). We need the pre-hook to fire BEFORE the write. This is done through `DispatchBeforeWrite` in World now (added in Task 2). No changes needed to ValueChangeBucket.

But we need to make sure the pre-hook fires for ALL Set paths:
- `World.Set<T>()` → `ApplyTypedSet` → Done (Task 2)
- CommandStream.Submit → `ComponentStore<T>.ApplyToWorld` → Need to add pre-hook
- Replay (FrameDelta) → `ApplyRawSet` → Done (Task 2)
- `EntityAccessor.Set<T>()` → Not hooked (explicitly excluded, uses `SetComponentAtTypedNoTrack`)

**Files:**
- Modify: `src/MiniArch/Core/CommandStreamCore.cs`

**Step 1: Add pre/post hooks to ApplyToWorld**

In `ComponentStore<T>.ApplyToWorld`, around the SetComponentAtTyped calls:

```csharp
// Before write
world.DispatchBeforeWrite(entry.Entity, arch, record.RowIndex);

// Write
if (!fastIsChunked)
    arch.SetComponentAtFlat<T>(...);
else
    arch.SetComponentAtTyped(...);

// After write
world.DispatchAfterWrite(entry.Entity, arch, record.RowIndex);
```

Four locations (tracking fast path, no-track fast path, mixed KindSet tracking, mixed KindSet no-track). All need pre/post hooks.

**Step 2: Build and run tests**

Run: `dotnet build -c Release --nologo` + `dotnet test -c Release --no-build --nologo`
Expect: pass

**Step 3: Commit**

```bash
git add src/MiniArch/Core/CommandStreamCore.cs
git commit -m "feat: pre/post write hooks in CommandStream.ApplyToWorld"
```

---

### Task 6: Migrate tests from old Track<T>() to new Track().Capture<T>()

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs` (10+ occurrences)
- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryFilterTests.cs` (26+)
- Modify: `tests/MiniArch.Tests/Core/ChangeTrackingInfrastructureTests.cs` (14+)
- Modify: `tests/MiniArch.Tests/Core/ChangeTrackingReplayTests.cs` (6+)
- Modify: `tests/MiniArch.Tests/Persistence/ChangeTrackingSnapshotTests.cs` (5+)
- Modify: `tests/MiniArch.Benchmarks/SetTrackingBenchmark.cs` (2)

**Step 1: For each test file, replace patterns:**

```csharp
// Before
var q = world.Track<Position>();

// After
var q = world.Track().Capture<Position>();
```

```csharp
// Before
q.ModifiedChunks()       // non-generic, uses class T

// After
q.ModifiedChunks<Position>()  // generic, specify T
```

```csharp
// Before
q.WithPreviousValues();
q.Changes()              // returns ValueChange<Position>[]

// After
q.Previous();
q.Changes()              // returns EntityChange[]
```

For tests that specifically test `ValueChange<T>` / `WithPreviousValues()`:
- `ChangeTrackingInfrastructureTests` has tests for WithPreviousValues behavior
- These need to become tests against `Changes()` with `Previous()` enabled

**Step 2: Build**

Run: `dotnet build -c Release --nologo`
Expect: success (once all test files updated)

**Step 3: Run tests**

Run: `dotnet test -c Release --no-build --nologo`
Expect: all 800+ pass

**Step 4: Commit**

```bash
git add tests/
git commit -m "tests: migrate to new Track().Capture<T>() API"
```

---

### Task 7: Delete old code

**Files to delete:**
- `src/MiniArch/ValueChange.cs` (public `ValueChange<T>`)
- Remove `ChangeQuery<T>` from `ChangeQuery.cs`
- Remove `World.Track<T>()` from `World.cs`

**Step 1: Remove old types**

ValueChange.cs — just delete the file.
ChangeQuery.cs — remove the old `ChangeQuery<T>` class text (whole file is new class now).
World.cs — remove `Track<T>()` method, keep only `Track()`.

**Step 2: Clean up IValueChangeSink**

If `IValueChangeSink<T>` is now only used by the internal `Handler<T>` approach, keep it internal. If we removed all `IValueChangeSink<T>` usage, delete the interface too. But since ChangeQuery no longer implements `IValueChangeSink<T>` directly (the old generic class did), we may need it for the internal handler pattern. Keep it for now, make it internal-only.

**Step 3: Build**

Run: `dotnet build -c Release --nologo`
Expect: clean build

**Step 4: Run tests**

Run: `dotnet test -c Release --no-build --nologo`
Expect: pass

**Step 5: Run soak**

Run: `dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 50000 --quiet`
Expect: 32/32 PASS

**Step 6: Run gate**

Run: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
Expect: Movement ≥ 1642, Attack ≥ 997, Memory OK

**Step 7: Commit**

```bash
git rm src/MiniArch/ValueChange.cs
git add src/MiniArch/ChangeQuery.cs src/MiniArch/Core/World.cs
git commit -m "feat: remove old ChangeQuery<T>, ValueChange<T> — migration complete"
```

---

### Task 8: Update knowledge base

**Files:**
- Modify: `.knowledge/kb-change-tracking.md`

Rewrite the Architecture section to reflect:
- Two entry points collapsed into one (`Track()`)
- `.Capture<T>()` replaces implicit With<T>
- `.Previous()` replaces `WithPreviousValues()`
- `.Changes()` returns `EntityChange[]` not `ValueChange<T>[]`
- `ModifiedChunks<T>()` at method level
- Internal Handler pattern
- Pre-hook dispatch for old-value capture

**Step 1: Edit kb-change-tracking.md**

Focus on: changed API surface, remove references to `ChangeQuery<T>` and `WithPreviousValues()`. Add `EntityChange`/`EntitySnapshot` docs.

**Step 2: Commit**

```bash
git add .knowledge/kb-change-tracking.md
git commit -m "doc: kb-change-tracking updated for unified ChangeQuery"
```

---

### Verification

After all tasks:

1. `dotnet build -c Release` → clean
2. `dotnet test -c Release --nologo` → all 800+ pass
3. `dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 50000 --quiet` → 32/32 PASS
4. `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` → no regression
