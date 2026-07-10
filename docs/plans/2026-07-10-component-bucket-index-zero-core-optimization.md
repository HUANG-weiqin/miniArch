# ComponentBucketQuery Zero-Core Optimization Implementation Plan

> **Status: SUPERSEDED — design direction abandoned.**

## Final Outcome

This plan proposed optimizing `ComponentBucketQuery<TComponent>` AutoFreshness via fingerprint reuse (Task 1) and adaptive direct rebuild (Task 2). During implementation, a simpler and more correct approach emerged:

**Deterministic per-key scan replaced the entire AutoFreshness/fingerprint/adaptive framework.**

Key reasons for the pivot:
1. **Probabilistic correctness was unacceptable.** Fingerprint-based freshness checks have inherent false-negative risk (different entity sets producing the same fingerprint). Even a 1-in-2^64 risk is philosophically wrong for an index that claims to be fresh.
2. **Complexity was not worth the gain.** The fingerprint + adaptive state machine added ~150 lines of stateful logic. The deterministic per-key scan is ~50 lines of straightforward iteration.
3. **Deterministic scan performed better than expected.** Without the fingerprint validation overhead, the deterministic scan exceeded the 80% target (reaching ~100-1500% of ManualExpanded across all scenarios).

**Final design** (see `.knowledge/kb-component-bucket-index-mvp-report.md` for full report):
- Every public read (`Get`, `TryGet`, `ContainsKey`, `Count`) performs a deterministic World scan for the requested key only.
- No fingerprint, no count fast-path, no dirty mode, no `Refresh()`.
- No internal buffer — caller provides `Span<Entity>` to `Get`/`TryGet`. No Bucket class, no Dictionary, no `_buffer`/`_count` fields.
- Constructor uses `scope.GetRequiredTypes()` instead of `RequiredTypes.ToArray()` to avoid cold-path allocation.
- GC: CountOnly/Read2/Write3 at 0.0 B/round steady state; AutoFreshness allocation is from benchmark's own `List<(Entity, CardZone)>`, not the query.
- Correctness is deterministic — no probabilistic false negatives.
- `dotnet test -c Release --nologo -v q`: 900 MiniArch + 5 HeroPipeline pass.
- `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`: Movement 1681.4, Attack 1182.4, memory OK.

---

*Below is the original plan preserved for historical reference.*

---

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Optimize `ComponentBucketQuery<TComponent>` auto-freshness without modifying `src/MiniArch/Core/`.

**Architecture:** Keep real component values as the only fact source. Keep the public API unchanged. Optimize only the user-layer cache validation/rebuild path in `src/MiniArch/ComponentBucketQuery.cs` by removing redundant work and adapting to repeated same-count value mutations.

**Tech Stack:** C#/.NET, MiniArch ECS public `World.Query`/chunk APIs, Release-mode perf harnesses.

---

## Hard Constraints

- Do not modify any file under `src/MiniArch/Core/`.
- Do not add manual sidecar maintenance APIs.
- Do not require users to call `Refresh()` for correctness.
- Keep `ComponentBucketQuery<TComponent>` public API unchanged.
- Real ECS component values remain the single fact source.

## Success Criteria

- Correctness: `ComponentBucketQueryTests` pass.
- Regression: `dotnet test -c Release --nologo -v q` passes.
- Architecture gate: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` passes.
- Perf: `AutoFreshness_Bucket` improves materially; target is at least 80% of `ManualExpanded` if zero-core constraints allow it.
- Static read scenarios (`CountOnly`, `Read2`, `Write3`) do not materially regress.

## Task 1: Reuse Known Fingerprint During Rebuild

**Files:**
- Modify: `src/MiniArch/ComponentBucketQuery.cs`
- Validate: `tests/MiniArch.Tests/UserApi/ComponentBucketQueryTests.cs`
- Measure: `tools/perf/ComponentBucketIndex.Perf/Program.cs` (project folder name unchanged)

**Steps:**
1. Change private `Rebuild()` to accept an optional known fingerprint, e.g. `private void Rebuild(bool hasKnownFingerprint = false, ulong knownFingerprint = 0)`.
2. In `EnsureFresh()`, when `ComputeFingerprint()` detects a mismatch, call `Rebuild(hasKnownFingerprint: true, knownFingerprint: fp)`.
3. In `Rebuild()`, skip per-entity fingerprint hashing when `hasKnownFingerprint` is true, but still rebuild buckets from the real world.
4. Preserve normal `Refresh()` and structural-count-change rebuild behavior: they must still compute fingerprint during rebuild.
5. Run targeted tests and benchmark.

**Expected impact:** AutoFreshness removes duplicate hash work from the rebuild pass. Correctness risk is low because the known fingerprint was just computed from the same world state that triggered rebuild.

## Task 2: Adaptive Direct Rebuild for Repeated Same-Count Dirtiness

**Files:**
- Modify: `src/MiniArch/ComponentBucketQuery.cs`
- Validate: `tests/MiniArch.Tests/UserApi/ComponentBucketQueryTests.cs`
- Measure: `tools/perf/ComponentBucketIndex.Perf/Program.cs` (project folder name unchanged)

**Steps:**
1. Add a private adaptive flag/counter recording that the previous same-count freshness check found a dirty fingerprint.
2. In `EnsureFresh()`, after count matches and the adaptive state says same-count dirtiness is likely, skip `ComputeFingerprint()` and call `Rebuild()` directly.
3. `Rebuild()` must compute and store the new fingerprint in this path.
4. When a fingerprint check finds no change, reset/decay the adaptive state so static read workloads do not permanently rebuild every read.
5. When structural count changes, rebuild and reset/adjust adaptive state conservatively.
6. Run targeted tests and benchmark.

**Expected impact:** AutoFreshness high-churn scenario goes from fingerprint scan + rebuild scan to rebuild scan only after the first dirty detection. This is the main path that can plausibly reach the 80% target while keeping zero core intrusion.

## Task 3: Cheap Internal Cleanup

**Files:**
- Modify: `src/MiniArch/ComponentBucketQuery.cs`

**Candidate changes:**
- Avoid allocating a new `List<TComponent>` for empty-key cleanup on every rebuild by reusing a private list or removing only when needed.
- Factor fingerprint hashing into a small helper to avoid duplicated code and reduce maintenance risk.
- Consider fast paths for common component sizes only if benchmark evidence shows hash cost remains significant.

**Rule:** Do not add complexity unless benchmark results show the previous two tasks are insufficient.

## Task 4: Verification and Knowledge Update

**Files:**
- Modify: `.knowledge/kb-component-bucket-index-mvp-report.md`
- Modify as needed: `.knowledge/INDEX.md`

**Commands:**
```bash
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~ComponentBucketQueryTests"
dotnet test -c Release --nologo -v q
dotnet run -c Release --project tools/perf/ComponentBucketIndex.Perf/ComponentBucketIndex.Perf.csproj
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**Expected output:** Tests pass; perf report includes before/after ratios and an explicit verdict on whether zero-core optimization reached the 80% target.
