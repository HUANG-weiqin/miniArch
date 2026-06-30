# MiniArch Command Buffer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a multi-producer command buffer that records structural changes and hierarchy changes without mutating `World`, compiles them into `FrameCommands`, and replays them with fixed ordering `create -> AddChild/RemoveChild -> add -> set -> remove -> destroy`.

**Architecture:** Add a new `MiniArch.Core.CommandBuffer` recording layer with thread-local shards and a real-entity allocator that can reserve both fresh ids and recycled ids without immediately materializing them into the world. `Playback()` compiles recorded commands into an immutable `FrameCommands` IR, eliminates `create + destroy` pairs in the same frame, precomputes final signatures for newly created entities, carries hierarchy `AddChild/RemoveChild` operations in a dedicated post-create phase, and leaves actual world mutation to `World.Replay(in FrameCommands)`.

**Tech Stack:** C# 12, .NET 8, xUnit, PowerShell

---

### Task 1: Re-read context and create the test file skeleton

**Files:**
- Read: `E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md`
- Read: `E:/godot/arch/miniArch/.knowledge/kb-command-buffer-feasibility.md`
- Read: `E:/godot/arch/miniArch/.knowledge/kb-hierarchy-runtime.md`
- Read: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Re-read the relevant knowledge pages**

Run:
```powershell
Get-Content E:/godot/arch/miniArch/.knowledge/kb-command-buffer-feasibility.md
```

Expected: the pages describe fixed replay order, real entity reservation during recording, hierarchy side-table semantics, `FrameCommands`, and `create + destroy` elimination.

**Step 2: Create the test class skeleton**

- Add `CommandBufferTests`
- Add local component types such as `Position`, `Velocity`, `Health`
- Keep all comments and identifiers in English

**Step 3: Commit the empty test scaffold**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs
git commit -m "test: scaffold command buffer coverage"
```

### Task 2: Lock the public contract of `Playback()` and `Replay()`

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/FrameCommands.cs`

**Step 1: Write the failing tests**

- Add a test that `CommandBuffer.Playback()` returns a `FrameCommands` value without mutating the world yet.
- Add a test that `World.Replay(in frameCommands)` applies the compiled frame and makes entities visible to queries.
- Add a test that `Replay()` can be called only once per compiled `FrameCommands`, or explicitly document and test repeatable replay if that is the chosen contract.

**Step 2: Run the targeted tests to verify they fail**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

Expected: fail because `CommandBuffer`, `FrameCommands`, and `World.Replay` do not exist yet.

**Step 3: Add the minimal API surface**

- Add `CommandBuffer`
- Add `FrameCommands`
- Add `World.Replay(in FrameCommands frameCommands)`
- Add only enough implementation to compile

**Step 4: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

Expected: compile succeeds, behavior tests still fail.

**Step 5: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs E:/godot/arch/miniArch/src/MiniArch/Core/FrameCommands.cs E:/godot/arch/miniArch/src/MiniArch/Core/World.cs
git commit -m "feat: add command buffer api skeleton"
```

### Task 3: Add real-entity reservation for recording

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferEntityAllocator.cs`

**Step 1: Write the failing tests**

- Add a test that `CommandBuffer.Create()` returns a real `Entity` immediately.
- Add a test that a newly returned entity can be referenced by later recorded `AddChild/RemoveChild/Add/Set/Remove/Destroy` commands in the same buffer.
- Add a test that after a previous replay destroys entities, a later command buffer create can reuse recycled ids rather than growing only through fresh ids.

**Step 2: Run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Create" -v minimal
```

Expected: fail because recording still does not reserve ids correctly.

**Step 3: Implement the allocator**

- Extract entity reservation logic out of the `World.Create` path
- Support both:
  - fresh ids via monotonic reservation
  - recycled ids via batch leasing from the world free pool
- Do not materialize `_locations` for reserved-only entities during recording
- Do not allow ids destroyed in the current in-flight buffer to become available for new creates in that same buffer

**Step 4: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Create" -v minimal
```

Expected: pass.

**Step 5: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs E:/godot/arch/miniArch/src/MiniArch/Core/World.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferEntityAllocator.cs
git commit -m "feat: reserve real entities for command buffer recording"
```

### Task 4: Implement thread-local recording shards and fixed-order compilation

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferShard.cs`

**Step 1: Write the failing tests**

- Add a test that recording order does not define final execution order.
- Record commands in user order such as `destroy`, `set`, `AddChild`, `add`, `create`, `RemoveChild`, `remove`.
- Assert that compilation/replay behaves as if order were always `create -> AddChild/RemoveChild -> add -> set -> remove -> destroy`.
- Add a test that `AddChild/RemoveChild` are compiled after `create`, so same-frame newly created entities can participate in hierarchy replay.
- Add a test that duplicate commands in the same bucket are folded to the intended final form for the same entity and component.

**Step 2: Run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~order" -v minimal
```

Expected: fail because recording is still linear or missing.

**Step 3: Implement shard-based recording**

- Give each recording thread a local shard
- Store per-bucket command arrays or lists
- Avoid a shared hot-path lock
- Merge shards during `Playback()`

**Step 4: Implement fixed-order compilation**

- Compile buckets in fixed order:
  - `create`
  - `AddChild/RemoveChild`
  - `add`
  - `set`
  - `remove`
  - `destroy`
- Do not preserve user order across buckets

**Step 5: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~order" -v minimal
```

Expected: pass.

**Step 6: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferShard.cs
git commit -m "feat: add shard-based command recording"
```

### Task 5: Precompute final signatures for created entities and eliminate `create + destroy`

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/FrameCommands.cs`

**Step 1: Write the failing tests**

- Add a test that a newly created entity with same-frame `Add/Set/Remove` lands directly in its final archetype after replay.
- Add a test that a newly created parent and child can be linked in the same frame and are visible as parent/child after replay.
- Add a test that same-frame `AddChild` followed by `RemoveChild` results in no live AddChild after replay.
- Add a test that same-frame `create + destroy` is removed during `Playback()` and never materializes in `Replay()`.
- Add a test that a created entity with `Add(Position)` then `Set(Position)` ends with the `Set` value.
- Add a test that a created entity with `Add(Position)` then `Remove(Position)` ends without `Position`.

**Step 2: Run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~created" -v minimal
```

Expected: fail because created entities are not compiled to final state yet.

**Step 3: Implement created-entity prepass**

- For each created entity, compute:
  - whether it survives the frame
  - final signature after `add -> set -> remove`
  - create-time component payloads needed to instantiate directly in the final archetype
- Preserve enough information for later `AddChild/RemoveChild` compilation against surviving created entities
- Remove `create + destroy` pairs from the resulting `FrameCommands`

**Step 4: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~created" -v minimal
```

Expected: pass.

**Step 5: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs E:/godot/arch/miniArch/src/MiniArch/Core/FrameCommands.cs
git commit -m "feat: compile created entities to final archetypes"
```

### Task 6: Implement `Replay(in FrameCommands)` with batch world mutation

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Archetype.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Chunk.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/HierarchyTable.cs`

**Step 1: Write the failing tests**

- Add a test that replay materializes created entities directly into the final archetype.
- Add a test that replay applies `AddChild` and `RemoveChild` after create and before structural component mutation buckets.
- Add a test that linking a created child to a created parent in the same frame succeeds.
- Add a test that unlinking an entity in the same frame removes the relation before possible destroy.
- Add a test that replay applies `add`, then `set`, then `remove`, then `destroy` to existing entities.
- Add a test that query results are correct only after replay, not after playback.

**Step 2: Run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Replay" -v minimal
```

Expected: fail because `Replay` is still incomplete.

**Step 3: Implement replay**

- Add a batch world mutation path that:
  - creates final-form entities first
  - applies AddChild and RemoveChild commands
  - applies add commands to existing entities
  - applies set commands
  - applies remove commands
  - destroys entities last
- Avoid per-command query invalidation
- Publish query layout visibility once at the end of replay
- Keep hierarchy side-table mutation separate from query layout invalidation because hierarchy does not participate in archetype matching

**Step 4: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Replay" -v minimal
```

Expected: pass.

**Step 5: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs E:/godot/arch/miniArch/src/MiniArch/Core/World.cs E:/godot/arch/miniArch/src/MiniArch/Core/Archetype.cs E:/godot/arch/miniArch/src/MiniArch/Core/Chunk.cs
git commit -m "feat: replay compiled frame commands"
```

### Task 7: Lock multi-producer correctness and free-list reuse

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferEntityAllocator.cs`

**Step 1: Write the failing tests**

- Add a test that multiple tasks can record into one command buffer concurrently and produce the same final world as a single-thread reference compilation.
- Add a test that recycled ids are reused across frames after replayed destroy commands.
- Add a test that same-frame destroyed ids are not reused by same-frame creates.
- Add a test that concurrent recording of `AddChild/RemoveChild` targeting valid entities produces the same hierarchy as a single-thread reference compilation.

**Step 2: Run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~concurrent" -v minimal
```

Expected: fail because shard merge and allocator edge cases are incomplete.

**Step 3: Implement the missing concurrency pieces**

- Finalize thread registration / shard discovery
- Finalize batch leasing from recycled ids
- Ensure concurrent recording does not drop or duplicate commands

**Step 4: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~concurrent" -v minimal
```

Expected: pass.

**Step 5: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/CommandBufferTests.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBuffer.cs E:/godot/arch/miniArch/src/MiniArch/Core/CommandBufferEntityAllocator.cs
git commit -m "feat: support concurrent command recording"
```

### Task 8: Add a benchmark for command buffer replay

**Files:**
- Create: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/Program.cs`
- Modify: `E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArchBenchmarkConfig.cs`

**Step 1: Write a small deterministic benchmark**

- One benchmark for recording only
- One benchmark for `Playback()`
- One benchmark for `Replay()`
- One mixed benchmark for `record + playback + replay`

**Step 2: Build the benchmark project**

Run:
```powershell
dotnet build E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release
```

Expected: pass.

**Step 3: Run a narrow benchmark filter**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/scripts/benchmark.ps1 --filter *CommandBuffer*
```

Expected: benchmark runs and reports command buffer numbers.

**Step 4: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/Program.cs E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArchBenchmarkConfig.cs
git commit -m "bench: add command buffer benchmarks"
```

### Task 9: Update docs and knowledge, then run full verification

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-command-buffer-feasibility.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/INDEX.md`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/README.md`

**Step 1: Document the new model**

- Explain `CommandBuffer`, `FrameCommands`, `Playback()`, and `Replay()`
- Explain fixed replay order
- Explain that hierarchy commands participate in replay as `AddChild/RemoveChild` immediately after `create`
- Explain that create returns a real entity during recording
- Explain that same-frame `create + destroy` is eliminated during playback
- Explain that concurrent support is only for recording, not for world mutation

**Step 2: Run the targeted command buffer tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

Expected: pass.

**Step 3: Run the full test suite**

Run:
```powershell
./scripts/test.ps1
```

Expected: pass.

**Step 4: Run the repo verification script**

Run:
```powershell
./scripts/verify.ps1
```

Expected: pass, or if existing unrelated failures remain in the dirty worktree, document them clearly before handoff.

**Step 5: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md E:/godot/arch/miniArch/.knowledge/kb-command-buffer-feasibility.md E:/godot/arch/miniArch/.knowledge/INDEX.md E:/godot/arch/miniArch/src/MiniArch/README.md
git commit -m "docs: document command buffer replay pipeline"
```
