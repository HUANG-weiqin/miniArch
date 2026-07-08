# Change Tracking API Redesign Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the confusing `Track().Capture<T>().Previous()` public API with two honest breaking-change APIs: shared component value logs and filtered structural transition logs.

**Architecture:** Keep the current high-performance runtime model: no-observer worlds keep `World.SharedTrackers == null`; value changes use one world-owned shared `ChangeTracker<T>` per component type; structural transitions use independent transition logs registered with the world. The API shape changes; the Set hot path must not regress.

**Tech Stack:** C#/.NET 8, xUnit, MiniArch ECS runtime, HeroComing.Perf gate.

---

## Design Contract

### Value changes

Public usage:

```csharp
var positions = world.TrackValueChanges<Position>();

ReadOnlySpan<ValueChange<Position>> changes = positions.Changes;
foreach (ref readonly var change in changes)
{
    _ = change.Entity;
    _ = change.Old;
    _ = change.New;
}

positions.ClearAll();
```

Semantics:

- `TrackValueChanges<T>()` arms value tracking for `T`; writes before arming are not retroactively captured.
- Tracks `World.Set<T>()` and `CommandStream.Set<T>()` only.
- Does not track `Create/Add/Remove/Destroy` or direct interior/ref writes.
- Multiple handles for the same `T` alias one world-shared per-component log.
- `SharedValueChanges<T>.Changes` is non-destructive and returns a zero-copy live span.
- `SharedValueChanges<T>.ClearAll()` clears the world-shared log for `T`, affecting all same-type handles.
- Same entity set multiple times before clear: first `Old` + latest `New`.

### Structural transitions

Public usage:

```csharp
var visible = world.TrackTransitions(
    new QueryDescription()
        .With<Renderable>()
        .Without<Hidden>());

ReadOnlySpan<Transition> transitions = visible.Transitions;
visible.Clear();
```

Semantics:

- `TrackTransitions(QueryDescription filter)` requires a non-empty filter and throws for an empty filter.
- Tracks query membership changes caused by `Create/Destroy/Add/Remove`.
- Does not track `Set<T>()`.
- `TransitionLog.Transitions` is non-destructive; `TransitionLog.Clear()` clears only that transition log.
- No parameterless transition tracking in this release. If lifecycle-only tracking is needed later, add an explicit `TrackLifecycleChanges()` API.

### Public removals / renames

Remove or make non-public before release:

- `World.Track()`
- `ChangeQuery`
- `Capture<T>()`
- `Previous()`
- `ChangeQuery.ValueChanges<T>()`
- `ChangeQuery.ClearChanges<T>()`
- `World.ClearChanges<T>()`

Rename:

- `TypedChange<T>` → `ValueChange<T>`

---

## Task 1: Add new value-change API tests first

**Files:**

- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs` or create `tests/MiniArch.Tests/UserApi/ValueChangeLogTests.cs`

**Step 1: Write failing tests**

Cover:

- `TrackValueChanges<T>()` captures direct `World.Set<T>()`.
- `TrackValueChanges<T>()` captures `CommandStream.Set<T>()`.
- `TrackValueChanges<T>()` does not retroactively capture writes before arming.
- Two `SharedValueChanges<T>` handles alias the same shared log and `ClearAll()` clears both.
- `TrackValueChanges<Position>()` and `TrackValueChanges<Velocity>()` remain independent.
- Same entity multiple sets before clear keep first old and latest new.
- `RestoreState()` preserves armed value handles for post-restore mutations.
- `WorldSnapshot.Load()` starts with no armed value tracking.

**Step 2: Run test to verify failure**

Run:

```bash
dotnet test "tests/MiniArch.Tests/MiniArch.Tests.csproj" -c Release --filter ValueChange
```

Expected: fails because new public types/methods do not exist.

---

## Task 2: Implement `ValueChange<T>` and `SharedValueChanges<T>`

**Files:**

- Rename or replace: `src/MiniArch/TypedChange.cs` → `src/MiniArch/ValueChange.cs`
- Create: `src/MiniArch/SharedValueChanges.cs`
- Modify: `src/MiniArch/ChangeTracker.cs`
- Modify: `src/MiniArch/Core/SharedTrackerRegistry.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/World.StructuralChange.cs`
- Modify: `src/MiniArch/Core/CommandStreamCore.cs`

**Implementation notes:**

- Rename all internal `TypedChange<T>` uses to `ValueChange<T>`.
- Add `public readonly struct SharedValueChanges<T> where T : unmanaged`.
- Store `World` and `ChangeTracker<T>` in the handle.
- Validate default handle and disposed world on `Changes` and `ClearAll()`.
- Add `World.TrackValueChanges<T>()` that lazy-creates `SharedTrackerRegistry` and `ChangeTracker<T>` using current `EntityCapacity`.
- Keep `World.SharedTrackers` nullable. Do not allocate registry unless `TrackValueChanges<T>()` is called.
- Keep `ChangeTracker<T>.Read()` non-destructive.

**Step 3: Run value tests**

Run:

```bash
dotnet test "tests/MiniArch.Tests/MiniArch.Tests.csproj" -c Release --filter ValueChange
```

Expected: value API tests pass.

---

## Task 3: Add new transition API tests first

**Files:**

- Modify or create: `tests/MiniArch.Tests/UserApi/TransitionLogTests.cs`

**Step 1: Write failing tests**

Cover:

- `world.TrackTransitions(new QueryDescription().With<Position>())` records create/add enter and destroy/remove exit.
- `TrackTransitions(empty QueryDescription)` throws with a helpful message.
- `TransitionLog.Transitions` is non-destructive until `Clear()`.
- `TransitionLog.Clear()` affects only that log, not another transition log.
- `RestoreState()` preserves transition logs for first post-restore mutation.

**Step 2: Run transition tests to verify failure**

Run:

```bash
dotnet test "tests/MiniArch.Tests/MiniArch.Tests.csproj" -c Release --filter TransitionLog
```

Expected: fails because new public type/methods do not exist.

---

## Task 4: Implement `TransitionLog`

**Files:**

- Create: `src/MiniArch/TransitionLog.cs`
- Modify: `src/MiniArch/Core/IChangeQuery.cs` if needed, or add a new internal interface if a narrower name is clearer.
- Modify: `src/MiniArch/Core/World.cs`

**Implementation notes:**

- Reuse existing transition dispatch mechanism (`World.RegisterChangeQuery`, weak references, `AppendTransition`).
- `TransitionLog` owns a `List<Transition>` and a `QueryDescription`/`QueryCache`.
- It registers with the world at construction.
- `Transitions` returns a `ReadOnlySpan<Transition>` over an internal array/list buffer without allocation. If using `List<T>`, expose via `CollectionsMarshal.AsSpan` internally.
- `Clear()` clears only its own transition buffer.
- On restore, clear stale transitions/cache but keep registration alive.
- Empty filter should throw from `World.TrackTransitions(filter)` before constructing the log.

**Step 3: Run transition tests**

Run:

```bash
dotnet test "tests/MiniArch.Tests/MiniArch.Tests.csproj" -c Release --filter TransitionLog
```

Expected: transition API tests pass.

---

## Task 5: Remove old public `ChangeQuery` surface and migrate tests/tools

**Files:**

- Modify/delete: `src/MiniArch/ChangeQuery.cs`
- Modify: all tests under `tests/MiniArch.Tests/UserApi/` and `tests/MiniArch.Tests/Persistence/ChangeTrackingSnapshotTests.cs`
- Modify: `tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs`
- Modify: `tools/perf/HeroComing.Perf/Program.cs`
- Modify docs/knowledge pages referencing old API.

**Implementation notes:**

- Either delete `ChangeQuery` or convert it into non-public implementation pieces. Public API should no longer expose `Capture`, `Previous`, `ValueChanges`, or `ClearChanges`.
- Migrate value tests to `TrackValueChanges<T>()`.
- Migrate transition tests to `TrackTransitions(QueryDescription)`.
- Update Hero `--track-observer` to use either no-op or new explicit APIs depending on intent. Since it currently validates capture-only inert behavior, adapt it to avoid creating any tracker unless explicitly measuring values.
- Update GameTickSim modified-chunks benchmark to use `TrackValueChanges<Position>()`, `.Changes`, `.ClearAll()`.

**Step 3: Run focused tests**

Run:

```bash
dotnet test "tests/MiniArch.Tests/MiniArch.Tests.csproj" -c Release --filter "ValueChange|TransitionLog|ChangeTrackingSnapshot"
```

Expected: focused tests pass.

---

## Task 6: Update knowledge base

**Files:**

- Modify: `.knowledge/kb-change-tracking.md`
- Modify: `.knowledge/kb-changelog.md`
- Modify: `.knowledge/kb-code-review-findings.md` only if new bug/safe finding is discovered
- Check: `.knowledge/INDEX.md`

**Content requirements:**

- Replace old `Track().Capture<T>().Previous()` examples with `TrackValueChanges<T>()`.
- Document `SharedValueChanges<T>` shared/global clear semantics.
- Document `TrackTransitions(QueryDescription)` as separate structural membership log.
- Document explicit non-support for filtered value changes and multi-capture value changes.
- Keep performance contract: no-observer `SharedTrackers == null`.

---

## Task 7: Final verification and performance proof

Run in this exact order:

```bash
dotnet test "miniArch.sln" -c Release
dotnet run -c Release --project "tools/perf/HeroComing.Perf" --check-baseline
dotnet run -c Release --project "tools/perf/HeroComing.Perf" -- --track-observer
dotnet run -c Release --project "tools/soak/MiniArch.Soak" -- --sweep 8 --frames 20000 --quiet
dotnet run -c Release --project "tools/perf/GameTickSim.Perf" -- --modified-chunks
git diff --check
```

Required pass criteria:

- All tests pass.
- Hero baseline: Movement ≥1642 rounds/s, Attack ≥997 rounds/s, memory OK.
- `--track-observer` still reports stable memory and no unintended transition/value work.
- Soak sweep 8/8 PASS.
- `git diff --check` has no whitespace errors; LF/CRLF warnings are acceptable.

---

## Task 8: Review and commit

Before committing:

```bash
git status --short --branch
git diff --stat
git diff
git log --oneline -10
```

Commit message:

```bash
git commit -m "feat(change-tracking): simplify public tracking API"
```
