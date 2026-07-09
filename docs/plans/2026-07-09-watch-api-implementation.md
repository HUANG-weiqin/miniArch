# Watch API — Pull-Event Change Tracking Implementation Plan

**Status:** Pending  
**Date:** 2026-07-09  
**Driver:** Replace old push-based change tracking with pull-event Watch API + delete all old infrastructure

---

## Overview

Replace `TrackValueChanges<T>()` + `TrackTransitions(QueryDescription)` + `CreateDenseValueDiff<...>()` with a unified pull-event Watch API (`Snapshot`/`Diff`). The old shared registry + push-based structural coupling is removed entirely.

---

## New API Surface

### Interfaces (3 files)

```
src/MiniArch/IChangeHandler.cs           — IChangeHandler<TComponent> + IChangeHandler<TComponent, TValue>
src/MiniArch/ITransitionHandler.cs       — TransitionKind enum + ITransitionHandler
```

### Handle types (3 files)

```
src/MiniArch/ChangeWatch.cs              — ChangeWatch<TComponent, THandler> (quick path)
src/MiniArch/ChangeWatch.Projected.cs    — ChangeWatch<TComponent, TValue, THandler> (full path)
src/MiniArch/TransitionWatch.cs          — TransitionWatch<THandler> (structural)
```

All handles expose:
- `Snapshot(World world)` — set baseline
- `Diff(World world)` — two-phase: collect + fire callbacks safely
- `ref THandler Handler` — read accumulated state

### World entry points (in World.cs)

```csharp
Watch<TComponent, THandler>(QueryDescription? query = null)
Watch<TComponent, TValue, THandler>(QueryDescription? query = null)
Watch<THandler>(QueryDescription filter)
```

---

## Phase 1: New API Files

### 1a. `src/MiniArch/IChangeHandler.cs`

```csharp
namespace MiniArch;

public interface IChangeHandler<TComponent>
    where TComponent : unmanaged, IEquatable<TComponent>
{
    void OnChange(World world, Entity entity, in TComponent oldValue, in TComponent newValue);
}

public interface IChangeHandler<TComponent, TValue>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
{
    TValue Project(in TComponent component);
    void OnChange(World world, Entity entity, TValue oldValue, TValue newValue);
}
```

### 1b. `src/MiniArch/ITransitionHandler.cs`

```csharp
namespace MiniArch;

public enum TransitionKind { Entered, Exited }

public interface ITransitionHandler
{
    void OnChange(World world, Entity entity, TransitionKind kind);
}
```

### 1c. `src/MiniArch/ChangeWatch.cs`

Implementation details:
- `sealed class ChangeWatch<TComponent, THandler>` where TComponent : unmanaged, IEquatable<TComponent>, THandler : struct, IChangeHandler<TComponent>
- Fields: `_oldValues (TComponent[])`, `_touchedEntities (int[])`, `_touchedCount`, `_query`, `_handler (THandler, non-readonly)`, `_hasSnapshot`
- Two-phase Diff buffer: `Entry[] _buffer` (Entity + TComponent Old + TComponent New) + `_bufCount`
- `Snapshot(World)` — scan query, project (if projected variant) or copy component (if quick), store in `_oldValues`, track touched
- `Diff(World)` — Phase 1: scan query into `_buffer`; Phase 2: iterate `_buffer`, call `_handler.OnChange`. Throws `InvalidOperationException` if `Snapshot` was never called.
- Consecutive `Snapshot` without intervening `Diff` clears previous touched slots (correct, performance cost)
- `ref THandler Handler => ref _handler`

### 1d. `src/MiniArch/ChangeWatch.Projected.cs`

- `ChangeWatch<TComponent, TValue, THandler>` with `where THandler : struct, IChangeHandler<TComponent, TValue>`
- `_oldValues` stores `TValue[]` (projected values, not components)
- Snapshot/Diff use `_handler.Project(span[i])` instead of direct component copy
- Otherwise identical structure to ChangeWatch

### 1e. `src/MiniArch/TransitionWatch.cs`

Implementation details:
- `sealed class TransitionWatch<THandler>` where THandler : struct, ITransitionHandler
- Fields: `_snapshot (Entity[])`, `_snapshotCount`, `_query`, `_handler`, `_hasSnapshot`
- Reusable `HashSet<int> _current` for Diff scan (cleared each call, no allocation after warmup)
- Diff buffer: `Entity[] _entered`, `_enteredCount`, `Entity[] _exited`, `_exitedCount`
- `Snapshot(World)` — scan query, store `Entity[]` with version
- `Diff(World)` — Phase 1: scan current query into `_current` (HashSet of IDs); find Entered (ID in current but not in snapshot) and Exited (ID in snapshot but not in current); Phase 2: call `_handler.OnChange` for each entry
- Exited entities pass the snapshot-time Entity (correct version from when they matched the filter)

---

## Phase 2: Modify World.cs

### Add 3 Watch methods (insert at old `TrackValueChanges` location)

See [New API Surface](#new-api-surface) above. All three call `AssertNotDisposed()`, auto-add `.With<TComponent>()` for null query in value paths, validate non-empty filter in transition path.

### Remove old code

| What | Where | Notes |
|---|---|---|
| `_trackingGeneration` | field | only used by old TransitionLog self-heal |
| `_changeQueries` | field + `RegisterChangeQuery` | old push dispatch |
| `SharedTrackers` | field + all usages | old shared registry |
| `ClearTypedTrackerSlots` | method | old tracker lifecycle |
| `CaptureRawTrackerBaseline` | method | old raw baseline capture |
| `CaptureTypedTrackerBaseline` | method | old typed baseline capture |
| `AppendTransition` | private method | old push dispatch |
| `TrackValueChanges<T>()` | public method | replaced by `Watch<TC, TH>()` |
| `TrackTransitions(QueryDescription)` | public method | replaced by `Watch<TH>(QueryDescription)` |
| `CreateDenseValueDiff<...>()` | public method | replaced by `Watch<TC, TV, TH>()` |
| Dispose cleanup | lines 143-148 | remove `_changeQueries.Clear`, `SharedTrackers?.Clear/ = null`, `_trackingGeneration++` |
| RestoreState cleanup | lines 1376-1390 | remove `_trackingGeneration++`, changeQueries dispatch loop, `SharedTrackers?.ResetAll()` |
| Line 1201 | `AppendTransition(entity, null, archetype)` | remove |

---

## Phase 3: Remove Structural Operation Coupling

### `World.StructuralChange.cs`

Remove these calls:
- `CaptureTypedTrackerBaseline(entity, componentType, in component)` (Add path)
- `AppendTransition(entity, sourceArchetype, destination!)` (Add path)
- `CaptureRawTrackerBaseline(entity, componentType, source)` (raw Add path)
- `AppendTransition(entity, sourceArchetype, destination)` (Remove path — this signature is `(entity, archetype, destination!)` with archetype being current)
- `ClearTypedTrackerSlots(entity.Id, componentType)` (Remove path)
- `AppendTransition(entity, archetype, destination!)` (Remove path — second signature variant)

### `World.EntityLifecycle.cs`

Remove these calls:
- `ClearTypedTrackerSlots(entity.Id)` (Destroy path)
- `AppendTransition(entity, arch, null)` (Destroy path)
- `AppendTransition(entity, null, archetype)` (Create path)

### `World.Create.Generated.cs`

Remove:
- Lines 683-684: `if (world?.SharedTrackers is not null) world.CaptureTypedTrackerBaseline(...)`

### Verify zero residual

After Phase 3:
```bash
rg "SharedTrackers|AppendTransition|IChangeQuery|_changeQueries|RegisterChangeQuery|ClearTypedTrackerSlots|CaptureRawTrackerBaseline|CaptureTypedTrackerBaseline" src/MiniArch/
```
→ Must return 0 hits.

---

## Phase 4: Delete 10 Old Files

```
src/MiniArch/ChangeTracker.cs
src/MiniArch/SharedValueChanges.cs
src/MiniArch/ValueChange.cs
src/MiniArch/TransitionLog.cs
src/MiniArch/Transition.cs                         ← TransitionKind moved to ITransitionHandler.cs
src/MiniArch/Core/IChangeQuery.cs
src/MiniArch/Core/SharedTrackerRegistry.cs
src/MiniArch/ChangeTracking/DenseValueDiff.cs
src/MiniArch/ChangeTracking/IValueProjector.cs
src/MiniArch/ChangeTracking/IValueChangeSink.cs
src/MiniArch/ChangeTracking/                       ← empty directory, delete
```

---

## Phase 5: Update XML doc References

### `CommandStream.cs` + `ParallelCommandStream.cs`

Replace XML doc `<see cref>` references:
- `SharedValueChanges{T}.Changes` → reference the Watch API instead
- `TransitionLog.Transitions` → reference `TransitionWatch<TH>.Diff`

---

## Phase 6: Rewrite Tests

### File mapping

| Old file | New file / disposition |
|---|---|
| `ChangeQueryTests.cs` | → `WatchTests.cs` (quick path + projected path semantics) |
| `ChangeQueryFilterTests.cs` | → `TransitionWatchTests.cs` |
| `ExplicitDenseValueDiffTests.cs` | → `WatchProjectedTests.cs` |
| `ChangeTrackingInfrastructureTests.cs` | → merge into `WatchTests.cs` |
| `ChangeTrackingReplayTests.cs` | → merge into `TransitionWatchTests.cs` |
| `ChangeTrackingSnapshotTests.cs` | → `TransitionWatchSnapshotTests.cs` |
| `TrickyEdgeCaseTests.cs` | update the 2 lines referencing old API |

### Required test coverage

**ChangeWatch (quick path):**
- Snapshot → Diff → no changes: callback not fired
- Snapshot → Set → Diff: callback fired with correct Old/New
- Capture twice: fresh baseline, previous diff not repeated
- Multiple Diff calls on same baseline: each fires callbacks
- Add entity after Snapshot: reported (stale slot semantics)
- Remove/Destroy entity after Snapshot: not reported
- Destroy+recreate stale slot: old slot value reported as Old
- Diff before Snapshot: throws `InvalidOperationException`
- null World: throws `ArgumentNullException`
- Disposed World: throws `ObjectDisposedException`

**ChangeWatch (projected path):**
- Same as quick path
- Project filters correctly (only selected field triggers change)
- Multiple independent watches on same component

**TransitionWatch:**
- Snapshot → Diff → no changes: callback not fired
- Create matching entity → Snapshot → Diff: no callback (entity in baseline)
- Create matching entity after Snapshot → Diff: Entered fired
- Destroy matching entity after Snapshot → Diff: Exited fired
- Remove component from matching entity → Diff: Exited fired
- Add matching component to non-matching entity → Diff: Entered fired
- Same entity enters and exits: both fired
- Snapshot → ex → re-enter → Diff: net Entered only
- Diff before Snapshot: throws
- null/empty filter: throws

**Mutation safety:**
- OnChange can call `world.Destroy(entity)` without corrupting iteration (two-phase design)
- OnChange can call `world.Add/Remove` without corrupting iteration

---

## Phase 7: Update Perf Benchmarks

### `tools/perf/HeroComing.Perf/Program.cs`

- Remove `--track-observer` path (old `TrackValueChanges<T>()`)
- Remove `--compare-old-value-tracking` four-way comparison (keep ManualDense/ManualDict)
- Remove projector structs (`PositionQProjector`, `PositionRProjector`, `HpProjector`)
- Remove `ChecksumSink`
- Remove `CreateDenseValueDiff` code paths
- Update baseline comparison logic

### `tests/MiniArch.Benchmarks/SetTrackingBenchmark.cs`

- Replace `_valueTracker = world.TrackValueChanges<Position>()` with `Watch<Position, Handler>()`
- Replace `_transitionLog = world.TrackTransitions(...)` with `Watch<Handler>(...)`

### `tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs`

- Replace `TrackValueChanges<Position>()` → `Watch<Position, Handler>()`
- Replace `SharedValueChanges<Position>` → `ChangeWatch<Position, Handler>`

---

## Phase 8: Update Documentation

### Files to rewrite

| File | Action |
|---|---|
| `docs/api.md` | Rewrite change tracking section: `World.Watch`, `ChangeWatch`, `TransitionWatch`, `IChangeHandler`, `ITransitionHandler` |
| `docs/examples.md` | Replace old `Track<T>()` / `TransitionCause` / `Transition` examples with new Watch API |
| `.knowledge/INDEX.md` | Update change tracking description |
| `.knowledge/kb-change-tracking.md` | Full rewrite for Watch API pull-event model |
| `.knowledge/kb-changelog.md` | Add entry for this refactoring |
| `.knowledge/kb-hero-pipeline-regression.md` | Update comparison data (remove ExplicitDiff column from old table) |
| `.knowledge/kb-design-rationale.md` | Update §2.11 section |
| `.knowledge/kb-command-stream.md` | Update pending entity contract to reference Watch API |

---

## Phase 9: Verification

```bash
# 1. Build
dotnet build -c Release

# 2. All tests
dotnet test -c Release

# 3. Zero residual old API
rg "TrackValueChanges|TrackTransitions|SharedValueChanges|TransitionLog|ChangeTracker|IChangeQuery|SharedTrackerRegistry|DenseValueDiff|IValueProjector|IValueChangeSink" src/ tests/MiniArch.Tests tests/MiniArch.Benchmarks tools/perf
# → must return 0

# 4. Perf gate
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

---

## Execution Order

```
Phase 1 (new files)       ───┐
Phase 2 (World.cs)       ───┤
Phase 3 (remove coupling) ──┤  These four phases form one atomic change
Phase 4 (delete files)    ──┤
                              │
Phase 5 (XML doc)          ──┤  Independent, can start after Phase 1
                              │
Phase 6 (tests)            ──┤  Requires Phase 1 (new Watch types)
Phase 7 (perf)             ──┤  Same
Phase 8 (docs)             ──┤  Can start after Phase 1
                              │
Phase 9 (verify)           ──┤  Requires all previous phases
```

Phases 1-4 must be done together (inter-dependent). Phases 5-8 are independent of each other once Phase 1 is done.
