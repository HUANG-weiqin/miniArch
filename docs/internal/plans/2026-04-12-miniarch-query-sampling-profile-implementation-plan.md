# MiniArch Query Sampling Profile Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a reproducible query profiling harness that runs long enough for CPU sampling and can expose either hot traversal cost or cold matching cost without instrumenting `MiniArch.Core`.

**Architecture:** Extend `MiniArch.Benchmarks` with a dedicated `profile-query` CLI path. Reuse `BenchmarkWorldFactory` for deterministic worlds, parse a small option set, and execute a timed loop over either a warmed query or a fresh reflected query instance. Lock the CLI and runner contract with xUnit tests first, then add a PowerShell wrapper and knowledge updates.

**Tech Stack:** .NET 8, C#, xUnit, PowerShell, BenchmarkDotNet project as host executable.

---

### Task 1: Lock the profiling contract with tests

**Files:**
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryProfilingRunnerTests.cs`

**Step 1: Write the failing tests**

- Add a test that CLI parsing uses the expected defaults.
- Add a test that CLI parsing accepts explicit overrides for scenario, temperature, entity count, duration, warmup, and startup delay.
- Add a test that the runner executes a short workload and returns non-zero iterations and a checksum.

**Step 2: Run the targeted tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --filter FullyQualifiedName~QueryProfilingRunnerTests`

Expected: fail because the profiling types do not exist yet.

### Task 2: Add the profiling runner to the benchmark host

**Files:**
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/Program.cs`

**Step 1: Add query profiling option parsing**

- Introduce option types for scenario and temperature.
- Provide defaults that match the hotspot workflow.

**Step 2: Add the timed execution loop**

- Reuse `BenchmarkWorldFactory.CreateMiniComplexQueryWorld`.
- Implement hot mode with a warmed cached query.
- Implement cold mode by cloning a fresh `MiniArch.Core.Query` instance through reflection so refresh work stays visible.

**Step 3: Add CLI dispatch**

- Route `profile-query` to the profiling runner.
- Leave BenchmarkDotNet execution unchanged for normal benchmark invocations.

### Task 3: Add a script entry point for reruns

**Files:**
- Create: `E:/godot/arch/miniArch/scripts/profile-query.ps1`

**Step 1: Add a small wrapper around the benchmark project**

- Forward the main profiling parameters.
- Invoke the benchmark executable with the `profile-query` verb.

**Step 2: Keep the user-facing path simple**

- Default to Release.
- Allow extra arguments for sampler-specific reruns.

### Task 4: Verify the profiling path

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Run the targeted profiling tests**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --filter FullyQualifiedName~QueryProfilingRunnerTests`

Expected: pass.

**Step 2: Run a benchmark project build check**

Run: `dotnet build E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release`

Expected: pass.

**Step 3: Run a short end-to-end profiling command**

Run: `powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/profile-query.ps1 -DurationSeconds 1 -StartupDelaySeconds 0`

Expected: the runner prints process information and finishes with non-zero iterations.

### Task 5: Update knowledge and re-read it

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Record the profiling rerun path**

- Mention the dedicated query profiling wrapper script.
- Mention hot vs cold modes and the intended use for sampling.

**Step 2: Re-read the updated knowledge**

Run: `Get-Content E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

Expected: the page accurately describes the profiling entry point and when to use it.
