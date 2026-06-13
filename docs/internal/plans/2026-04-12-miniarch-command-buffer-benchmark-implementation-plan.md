# MiniArch Command Buffer Benchmark Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add end-to-end `record + play` command buffer benchmarks that compare `MiniArch` and `Arch` on shared structural-command scenarios, then optimize `MiniArch` until every compared time/allocation result stays within `1.5x`.

**Architecture:** Introduce a shared scenario model for public structural commands, use it both in parity tests and in BenchmarkDotNet setup, and keep setup/world construction outside the measured region. If benchmark results show any `>1.5x` regressions, iterate on `MiniArch` command buffer hot paths and rerun the same gates.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet, PowerShell

---

### Task 1: Add shared public-command benchmark scenarios

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarkScenarios.cs`
- Read: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write the failing tests**

- Add a new test file that executes the shared public-command scenarios against both `MiniArch` and `Arch`.
- Cover `dense-existing`, `create-heavy`, and `mixed-script`.
- Assert both engines complete successfully and produce the same structural summary.

**Step 2: Run the targeted tests to verify they fail**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferBenchmarkScenarioTests -v minimal
```

Expected: FAIL because the shared scenario layer does not exist yet.

**Step 3: Add the minimal shared scenario model**

- Add deterministic script definitions for:
  - `Create`
  - `Add`
  - `Set`
  - `Remove`
  - `Destroy`
- Add per-engine runners that translate the same script to `MiniArch` and `Arch` command buffer APIs.

**Step 4: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferBenchmarkScenarioTests -v minimal
```

Expected: PASS.

### Task 2: Add `MiniArch vs Arch` end-to-end benchmarks

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/Program.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArchBenchmarkConfig.cs`

**Step 1: Write the benchmark surface**

- Add paired benchmark methods for each shared scenario:
  - `Arch_*`
  - `MiniArch_*`
- Keep only `record + play` inside the benchmark body.
- Add params:
  - scenario
  - `EntityCount = 128 / 1000 / 10000`

**Step 2: Build the benchmark project**

Run:
```powershell
dotnet build E:/godot/arch/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release
```

Expected: PASS.

**Step 3: Run a narrow benchmark smoke**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release -- --filter *CommandBuffer*
```

Expected: benchmark runs and emits `MiniArch` and `Arch` results for the shared scenarios.

### Task 3: Add MiniArch-only hierarchy extension coverage

**Files:**
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferBenchmarkScenarioTests.cs`

**Step 1: Add the hierarchy extension scenario**

- Add a `MiniArch-only` mixed scenario with `Link / Unlink`.
- Keep it out of `Arch` comparison summaries and out of the `1.5x` gate.

**Step 2: Verify the scenario**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferBenchmarkScenarioTests -v minimal
```

Expected: PASS.

### Task 4: Evaluate the `1.5x` gate and optimize if needed

**Files:**
- Modify as needed:
  - `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs`
  - `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
  - `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferShard.cs`
  - `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferEntityAllocator.cs`

**Step 1: Run the full command buffer benchmark set**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release -- --filter *CommandBuffer*
```

Expected: complete output with time and allocation metrics for every `MiniArch vs Arch` scenario.

**Step 2: Identify regressions**

- Compare each shared scenario and each entity-count tier.
- Mark any result where `MiniArch` time or allocation is `>1.5x Arch`.

**Step 3: Add a failing regression test if behavior changed**

- If an optimization changes semantics, first write or extend a failing test.

**Step 4: Implement the minimal optimization**

- Optimize only the measured hot path.
- Prefer reducing transient allocations and duplicate per-command work inside `Play()`.

**Step 5: Re-run the relevant tests and benchmarks**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~CommandBufferBenchmarkScenarioTests" -v minimal
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release -- --filter *CommandBuffer*
```

Expected: all compared scenarios now satisfy the `1.5x` gate.

### Task 5: Update knowledge and record the benchmark contract

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-command-buffer-feasibility.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/INDEX.md`

**Step 1: Update knowledge pages**

- Document the new `MiniArch vs Arch` command buffer benchmark.
- Document the shared public-command scenarios and the `1.5x` acceptance gate.
- Note that hierarchy scenarios are `MiniArch-only` and excluded from cross-engine pass/fail.

**Step 2: Run verification**

Run:
```powershell
./scripts/verify.ps1
```

Expected: PASS, or a clearly documented unrelated blocker.
