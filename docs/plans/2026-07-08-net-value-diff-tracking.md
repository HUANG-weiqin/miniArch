# Net Value Diff Tracking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Status 2026-07-08:** Superseded by the boundary-diff implementation. The API and net diff semantics remain, but the approved internals changed from write-time dirty-log collapse to baseline-to-current scanning so `Set<T>` / `CommandStream.Set<T>` stay free of value-tracking work and ref/span direct writes are also tracked.

**Goal:** Keep `TrackValueChanges<T>()` API shape, but change internals from write-log semantics to net value diff semantics that match practical hand-written shadow trackers.

**Final Architecture:** `SharedValueChanges<T>` remains a world-shared per-component handle. `ChangeTracker<T>` now stores per-entity baselines and rebuilds `Changes` by scanning current world state. `Set<T>` and `CommandStream.Set<T>` do not query trackers or write dirty slots.

**Tech Stack:** .NET 8/C#, MiniArch ECS, xUnit, HeroComing.Perf.

---

## Semantics

`TrackValueChanges<T>()` tracks **net value changes since the last `ClearAll()`**:

| Writes before ClearAll | Result |
|---|---|
| `A -> A` | no change |
| `A -> B` | `{ Old=A, New=B }` |
| `A -> B -> C` | `{ Old=A, New=C }` |
| `A -> B -> A` | no change |

This intentionally does **not** expose every `Set<T>` call. If users need a write log, that should be a separate API, not `TrackValueChanges<T>()`.

---

### Task 1: Add failing tests for net value diff semantics

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs`
- Modify as needed: other existing change-tracking test files if this behavior is already covered differently

**Tests to add/update:**
1. `TrackValueChanges_ignores_noop_set`
2. `TrackValueChanges_collapses_multiple_sets_to_first_old_latest_new`
3. `TrackValueChanges_removes_change_when_value_returns_to_original`
4. CommandStream variant for revert-to-old, because Hero uses CommandStream Set path.

**Expected before implementation:**
- no-op may already pass due temporary `old==new` skip;
- revert-to-old should fail until swap-remove cancellation is implemented.

---

### Task 2: Implement net-cancel in `ChangeTracker<T>` ✅ superseded

This write-time dirty-log implementation was intentionally replaced by boundary diff. The net semantics below still describe externally visible behavior, but `SlotByEntityPlusOne` / dirty-log collapse no longer exists.

**Files:**
- Modify: `src/MiniArch/ChangeTracker.cs`
- Modify if necessary: `src/MiniArch/Core/World.StructuralChange.cs`

**Implementation:**
1. Move old==new skip into production code without TEMP comment.
2. In the existing per-entity slot path:
   - If entity not dirty and old != new: append `{ Entity, Old, New }`.
   - If entity already dirty: update `New` to latest value.
   - If updated `New` equals original `Old`: remove the change entry by swap-removing the last entry into the removed slot and updating `SlotByEntityPlusOne[moved.Entity.Id]`.
3. Clear the removed entity slot to zero.
4. Keep `Clear()` O(changes) by clearing only currently dirty slots.
5. Avoid allocations in steady state.

**Hot path constraints:**
- No dictionaries.
- No full world scan.
- No per-consumer fanout.
- One equality check for no-op and one equality check after updating an existing dirty entry are acceptable.

---

### Task 3: Run focused tests

**Command:**

```bash
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --filter "ChangeQueryTests|ChangeTracking"
```

If filter syntax misses tests, run the whole project:

```bash
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release
```

---

### Task 4: Update docs/knowledge semantics ✅

**Files:**
- Modify: `.knowledge/kb-change-tracking.md`
- Modify: `.knowledge/kb-hero-pipeline-regression.md`
- Modify if relevant: `.knowledge/kb-changelog.md`

**Content:**
- Define net value diff semantics explicitly.
- State that `TrackValueChanges<T>()` is not a write log.
- Update Hero comparison notes after measuring.

---

### Task 5: Measure Hero comparison ✅

**Commands:**

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf -- --compare-old-value-tracking
dotnet run -c Release --project tools/perf/HeroComing.Perf -- --track-observer
dotnet run -c Release --project tools/perf/HeroComing.Perf -- --check-baseline
```

**Acceptance:**
- API `Changes/round` matches generic manual shadow-diff in Movement and Attack.
- API throughput should be close to or above manual. If not, report the remaining gap and likely hot path cause.
- Baseline check passes.

**Final boundary-diff result:**
| Scenario | API rounds/s | ManualDense rounds/s | ManualDict rounds/s | API/Dense | API/Dict |
|---|---|---|---|---|---|
| Movement | 1595.4 | 1958.7 | 1916.9 | 0.814 | 0.832 |
| Attack | 1003.4 | 1193.3 | 1190.8 | 0.841 | 0.843 |

Conclusion: API is not faster than manual shadow diff in the Hero loops because `.Changes` scans the world each round. The value is unified API, Set/CommandStream/GetRef/chunk-span coverage, low/zero steady allocations, net diff semantics, no observer cost when unarmed, and no value-tracking branch in `Set<T>` / `CommandStream.Set<T>`.

---

### Task 6: Final verification ✅

**Commands:**

```bash
dotnet test miniArch.sln -c Release
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 8 --frames 20000 --quiet
git diff --check
```

**Do not commit** unless explicitly requested.
