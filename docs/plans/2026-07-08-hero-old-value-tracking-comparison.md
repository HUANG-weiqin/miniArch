# Hero Old-Value Tracking Comparison — Implementation Plan (Updated)

> **Status:** Implemented. Slot-port/write-point "cheating" manual tracker was replaced first by a generic dense shadow-diff scanner, then extended with a dict baseline to cover unknown/sparse-id scenarios. Latest run reflects the boundary-diff API implementation.

## Goal

Compare MiniArch `TrackValueChanges<T>()` old/new tracking against two generic manual shadow-diff observers that scan component values before/after each round using `FrameView.ChunkQuery`, without slot-port wrappers, without custom ports, and without Hero-specific write-point hooks:

- `ManualDense`: `entity.Id -> int[]`
- `ManualDict`: `Dictionary<int,int>`

## Rejection Note (2026-07-08)

The original Task 4 implemented a slot-port wrapper (`ManualTrackerPort`) that hooked `TryRead`/`Write` on Hero slot ports to capture old/new values. This was rejected because:

- It is Hero-specific, not generic: it relies on `IIntSlotPort`, `SlotKey`, and `ModifierApplySystem`'s read-before-write pattern.
- It reuses old values already read by `ModifierApplySystem` (cheating): it caches the `TryRead` result and uses it in `Write` without any own scan.
- A maintainable comparison should represent generic code that cannot locate/intercept every write site.

**Decision:** The manual trackers now use generic shadow-diff scans:
- `BeforeRound()`: scans `FrameView` for tracked component types, stores old values in tracker-owned storage.
- `Drain()`: scans `FrameView` again, compares new vs old, records only changed entities.
- No slot ports, no `IIntSlotPort`, no custom `CharacterTestFixture` constructor.
- `ManualGenericTracker<T>` = dense arrays + touched list for O(tracked) clear.
- `ManualDictionaryTracker<T>` = `Dictionary<int,int>` for unknown/sparse id range scenarios.

## Architecture

Keep the existing no-observer and `--track-observer` modes unchanged. Add a separate `--compare-old-value-tracking` mode that runs Movement and Attack with three observer strategies: MiniArch boundary-diff API, manual dense shadow-diff scanner, and manual dict shadow-diff scanner. All strategies must consume `Entity.Id`, old value, and new value into a printed checksum so the JIT cannot delete the read path.

## Changes

### Task 4 (rewritten): Implement manual dense/dict shadow-diff trackers

**Files:**
- Modify: `tools/perf/HeroComing.Perf/Program.cs`

**Steps:**
1. Add `BeforeRound()` to `TrackObserver` (default no-op).
2. Add `ManualGenericTracker<T>`:
   - `_frame` (FrameView), `_toInt` (Func<T, int>).
   - `_oldValues[]`: entity.Id → old int value.
   - `_touchedEntities[]` + `_touchedCount`: for O(tracked) clear.
   - `BeforeRound()`: scan all entities with `T` via `ChunkQuery`, store old values.
   - `Drain(TrackObserver)`: scan again, compare, record changes (Entity.Id, old, new) into observer's checksum.
   - `Clear()`: walk touched list to reset `_oldValues[id] = 0`.
3. Add `ManualDictionaryTracker<T>`:
   - `_oldValues: Dictionary<int,int>`.
   - Same `BeforeRound()/Drain()/Clear()` protocol, but keyed by dictionary rather than dense arrays.
4. Observer factories:
   - `CreateManualDenseMovementObserver`: two `ManualGenericTracker<PositionQValue>` and `ManualGenericTracker<PositionRValue>`.
   - `CreateManualDenseAttackObserver`: one `ManualGenericTracker<CurrentHpValue>`.
   - `CreateManualDictMovementObserver`: two `ManualDictionaryTracker<PositionQValue>` and `ManualDictionaryTracker<PositionRValue>`.
   - `CreateManualDictAttackObserver`: one `ManualDictionaryTracker<CurrentHpValue>`.
5. No slot-port wrappers, no custom ports, no `CharacterSlotKeys`.

### Task 5 (updated): Remove customPorts plumbing

**Files:**
- Modify: `tools/perf/HeroComing.Perf/Program.cs`

**Steps:**
1. Remove `customPorts` parameter from `RunScenarioInternal` — always use `new CharacterTestFixture()`.
2. Change `observerFactory` type from `Func<World, TrackObserver>` to `Func<MiniArchRuntime, TrackObserver>` (passes runtime so both API observers needing `World` and manual observers needing `CurrentFrame` can work).
3. Add `observer?.BeforeRound()` before `createRequests(...)` in both warmup and timed loops.
4. Remove `using Hero.GameplayEcs.Characters.Slots`.
5. Remove `ManualOldValueTracker`, `ManualTrackerPort`, `BuildManualPorts`.

### Latest run output after boundary diff

```
  Movement   | API          |     1595.4 |     0.627 |   47862 |    -2687.8 | 23931000 |  500.00 |   1271039927
  Movement   | ManualDense  |     1958.7 |     0.511 |   58762 |    -3019.4 | 29381000 |  500.00 |  -1093226894
  Movement   | ManualDict   |     1916.9 |     0.522 |   57508 |    -3019.4 | 28754000 |  500.00 |   1340478086

  Attack     | API          |     1003.4 |     0.997 |   30103 |    -2910.2 |    30103 |    1.00 |    996767598
  Attack     | ManualDense  |     1193.3 |     0.838 |   35798 |    -2910.1 |    35798 |    1.00 |   1562630319
  Attack     | ManualDict   |     1190.8 |     0.840 |   35725 |    -2910.1 |    35725 |    1.00 |    487034990
```

Note: After boundary diff, Movement reports 500 changes/round for all three strategies; `PositionR` no-op writes do not survive as API change entries. API still trails manual shadow diff in total throughput because `.Changes` scans the world each round; its value is broader write coverage and keeping `Set<T>` / `CommandStream.Set<T>` free of value-tracker work.
