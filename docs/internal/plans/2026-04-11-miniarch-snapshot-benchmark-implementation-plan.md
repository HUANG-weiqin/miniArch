# MiniArch Snapshot Benchmark Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a dedicated BenchmarkDotNet benchmark for snapshot save/load time and snapshot byte size across 100, 500, and 1000 entities with 10 unmanaged components per entity.

**Architecture:** Keep snapshot benchmarking separate from structural-change benchmarking. Build one deterministic benchmark world shape, prepare benchmark inputs in setup, and measure `WorldSnapshot.Save` and `WorldSnapshot.Load` independently while exporting snapshot size as a separate metric.

**Tech Stack:** .NET 8, C#, BenchmarkDotNet, MiniArch.Core.

---

## Task 1: Add the benchmark world shape

**Files:**
- Modify: `E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/BenchmarkComponents.cs`
- Modify: `E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/BenchmarkWorldFactory.cs`

**Step 1: Add the missing unmanaged benchmark components**

- Extend the benchmark component set to exactly 10 unmanaged component types.
- Keep them small and deterministic.

**Step 2: Add a factory for snapshot benchmark worlds**

- Add a helper that creates a `MiniWorld` and assigns all 10 components to every entity.
- Keep values deterministic from entity index.

**Step 3: Build the benchmark project**

Run: `dotnet build E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -v minimal`

Expected: build succeeds.

## Task 2: Add the dedicated snapshot benchmark

**Files:**
- Create: `E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/SnapshotBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/MiniArchBenchmarkConfig.cs`

**Step 1: Implement benchmark state and setup**

- Add `[Params(100, 500, 1000)]`.
- In setup, prepare:
  - a world for save
  - a byte array for load
  - a cached size metric

**Step 2: Add benchmark methods**

- `MiniArch_Snapshot_Save`
- `MiniArch_Snapshot_Load`

**Step 3: Export snapshot size**

- Add a BenchmarkDotNet column or equivalent exported metric so size appears in the result table.

**Step 4: Build the benchmark project**

Run: `dotnet build E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -v minimal`

Expected: build succeeds.

## Task 3: Verify the benchmark runs

**Files:**
- No additional production files required unless verification reveals a gap

**Step 1: Run only the snapshot benchmark**

Run: `dotnet run --project E:/godot/arch/miniArch-snapshot/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -- --filter *SnapshotBenchmarks*`

Expected: BenchmarkDotNet runs `Save` and `Load` for 100 / 500 / 1000 entities and exports size.

**Step 2: Update knowledge**

- Add the new benchmark entry point to the benchmark/test knowledge page if needed.

**Step 3: Summarize observed output**

- Report the benchmark names, parameter coverage, and where the exported results live.
