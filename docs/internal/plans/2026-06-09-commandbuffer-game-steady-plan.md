# CommandBuffer Game Steady-State Benchmark Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build an independent steady-state game benchmark comparing MiniArch, Friflo, and Arch command buffers against reused worlds/stores.

**Architecture:** Add a new `perf/CommandBufferGame.Perf` console project. Keep the scenario self-contained with shared component structs, one scenario interface, and three engine implementations that run the same deterministic tick schedule. Report total throughput, checksum, live count, GC, heap delta, and query/record/playback phase timings.

**Tech Stack:** .NET 8 console app, local MiniArch project reference, NuGet `Friflo.Engine.ECS` 3.6.0, NuGet `Arch` 2.1.0.

**Repository note:** Do not commit unless the user explicitly asks; the repository-level git rule overrides the generic commit steps from this planning skill.

---

### Task 1: Create perf project shell

**Files:**
- Create: `perf/CommandBufferGame.Perf/CommandBufferGame.Perf.csproj`
- Create: `perf/CommandBufferGame.Perf/Program.cs`

**Steps:**
1. Add the project file with `net8.0`, nullable enabled, package references for Friflo/Arch, and project reference to `src/MiniArch/MiniArch.csproj`.
2. Add a placeholder `Program.cs` that prints the benchmark name.
3. Run: `dotnet build -c Release perf/CommandBufferGame.Perf/CommandBufferGame.Perf.csproj`
4. Expected: build succeeds.

### Task 2: Add shared benchmark model

**Files:**
- Modify: `perf/CommandBufferGame.Perf/Program.cs`

**Steps:**
1. Add shared component structs implementing `Friflo.Engine.ECS.IComponent` for Friflo compatibility.
2. Add `ICommandBufferGameScenario`, `ScenarioResult`, `PhaseTicks`, and `BenchmarkRunner`.
3. Add deterministic constants: initial live count, spawn/despawn per tick, status churn per tick, mutation per tick, warmup/measure seconds.
4. Keep all allocations for schedules/queues outside hot inner measurement where practical.
5. Build in Release.

### Task 3: Implement MiniArch scenario

**Files:**
- Modify: `perf/CommandBufferGame.Perf/Program.cs`

**Steps:**
1. Setup one `MiniArch.World`, one reusable `MiniArch.Core.CommandBuffer`, one FIFO `Queue<MiniArch.Entity>`, and stable arrays for existing entity handles.
2. Initialize a mixed combat population with `Position`, `Velocity`, `Health`, `Team`, and optional statuses.
3. Per tick, run query pass over `Position + Velocity + Health`, record create/destroy/set/add/remove commands, then `Submit()`.
4. Maintain checksum and live count.
5. Build in Release.

### Task 4: Implement Friflo scenario

**Files:**
- Modify: `perf/CommandBufferGame.Perf/Program.cs`

**Steps:**
1. Setup one `EntityStore`, one command buffer from `store.GetCommandBuffer()`, `ReuseBuffer = true`, FIFO queue, and stable entity-id array.
2. Use `CreateEntity`, `AddComponent`, `RemoveComponent`, `DeleteEntity`, and `Playback()` through the command buffer.
3. Keep the same schedule and operation counts as MiniArch.
4. Build in Release.

### Task 5: Implement Arch scenario

**Files:**
- Modify: `perf/CommandBufferGame.Perf/Program.cs`

**Steps:**
1. Setup one `Arch.Core.World`, one reusable `Arch.Buffer.CommandBuffer`, FIFO queue, and stable `Arch.Core.Entity` array.
2. Use `Create`, `Add`, `Remove`, `Set`, `Destroy`, and `Playback(world, true)`.
3. Keep the same schedule and operation counts as MiniArch and Friflo.
4. Build in Release.

### Task 6: Add runner output and validation checks

**Files:**
- Modify: `perf/CommandBufferGame.Perf/Program.cs`

**Steps:**
1. Print scenario constants and a table for engine, ticks/s, ms/tick, checksum, live count, heap delta, GC counts, and phase percentages.
2. Add CLI knobs only if cheap: `--warmup`, `--measure`.
3. Ensure all engines run setup once, then warmup, forced GC, timed loop.
4. Run: `dotnet run -c Release --project perf/CommandBufferGame.Perf`
5. Expected: output contains MiniArch, Friflo, and Arch rows.

### Task 7: Update knowledge base

**Files:**
- Modify: `.knowledge/INDEX.md`
- Create: `.knowledge/kb-commandbuffer-game-perf.md`

**Steps:**
1. Add a `CommandBufferGame.Perf` module row and quick-entry AddChild.
2. Create a knowledge page using `.knowledge/_template.md` structure.
3. Document purpose, fairness rules, command, and pitfalls.
4. Verify `updated` is today's date.

### Task 8: Final verification

**Files:**
- No code changes unless verification finds a defect.

**Steps:**
1. Run: `dotnet build -c Release perf/CommandBufferGame.Perf/CommandBufferGame.Perf.csproj`
2. Run: `dotnet run -c Release --project perf/CommandBufferGame.Perf -- --warmup 1 --measure 1`
3. Because `src/MiniArch/` is not modified, the HeroComing architecture regression gate is not required by AGENTS.md.
4. Check `git diff` for accidental unrelated changes.
