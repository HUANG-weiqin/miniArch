# FrameLookup Chunk-Run Consumer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 在 ValueLab 中验证 `CompactRowLookup<TKey>` 的 chunk-run / batched consumer 是否能替代失败的逐 row DirectForEach。

**Architecture:** 只改 `tools/perf/FrameReadModels.ValueLab/**`。保留 compact CSR build，不新增 build-time run table；query 时按 key 的 contiguous row refs on-the-fly 合并为 same-chunk adjacent-row runs，再把 sliced spans 交给 struct consumer。

**Tech Stack:** .NET 8 / C# / MiniArch public `ChunkView` + `ReadOnlySpan<T>` / Release-only stopwatch perf harness / embedded correctness matrix。

---

## Task 1: Add run consumer shape

**Files:**
- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModels.cs`

**Steps:**
1. Add lab-only interfaces `IFrameRunConsumer<T1>`, `IFrameRunConsumer<T1,T2>`, `IFrameRunConsumer<T1,T2,T3>`.
2. Add `HealthSumRunConsumer1 : IFrameRunConsumer<Health>`.
3. Add `HealthSumRunConsumer3 : IFrameRunConsumer<Cell,Position,Health>` if needed for arity parity.
4. Run `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only`.
5. Commit: `perf: add frame lookup run consumer shape`.

## Task 2: Implement CompactRowLookup.ForEachRun

**Files:**
- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelLayouts.cs`

**Steps:**
1. Add `ForEachRun<T1,TConsumer>` to `CompactRowLookup<TKey>`.
2. Loop the key bucket from `_flatRows[start..start+count]`.
3. Coalesce while next row has same `ChunkIndex` and `RowIndex == previous + 1`.
4. Slice `chunk.GetEntities()` and `chunk.GetSpan<T1>()`, call `consumer.Accept(...)` once per run.
5. Add 2/3 arity overloads only by exact copy of the same loop with extra spans; do not generalize with delegates.
6. Return processed row count.
7. Run correctness-only.
8. Commit: `perf: add compact row chunk-run consume`.

## Task 3: Add correctness coverage

**Files:**
- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelCorrectness.cs`

**Steps:**
1. Add `RunForEachConsistency` case for CompactRow.
2. Compare `ForEachRun<Health>` sum against `CopyRowRefs + manual span read`.
3. Ensure data creates at least same-chunk contiguous runs and multiple keys.
4. Reuse existing multi-archetype/chunked coverage by running full matrix.
5. Run:
   - `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only`
   - `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000`
6. Commit: `test: cover frame lookup chunk-run consume`.

## Task 4: Add perf variant

**Files:**
- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelBenchmarks.cs`

**Steps:**
1. Add `CompactRowRunForEach` variant after `CompactRowDirectForEach`.
2. Build with the same `Rows<Cell,Position,Health>` path as CompactRowDsl.
3. Warm full consume paths before timing.
4. Measure row component consume with `ForEachRun<Health, HealthSumRunConsumer1>`.
5. Print summary ratio: `RunForEach vs CopyRows`.
6. Run:
   - `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick`
   - `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full`
7. Commit: `perf: measure compact row chunk-run consume`.

## Task 5: Report verdict and update KB

**Files:**
- Create: `docs/plans/2026-07-11-frame-lookup-chunk-run-report.md`
- Modify: `.knowledge/kb-frame-read-models.md`
- Modify: `.knowledge/INDEX.md` if wording changes are needed

**Steps:**
1. Record Go / Conditional Hold / No-Go for chunk-run.
2. Include quick/full numbers for CopyRowRefs, DirectForEach, RunForEach.
3. State whether production remains blocked.
4. Run `git diff --check`.
5. Commit: `docs: report frame lookup chunk-run verdict`.

## Task 6: Final verification

**Commands:**
```bash
dotnet build -c Release miniArch.sln
dotnet test -c Release miniArch.sln
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
git diff --check
git status --short --branch
git log --oneline -10
```

**Notes:**
- Do not run `--update-baseline`.
- Do not modify `src/MiniArch/**`.
- HeroComing.Perf is not required unless production files are touched.
