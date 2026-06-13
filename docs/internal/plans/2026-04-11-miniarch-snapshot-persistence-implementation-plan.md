# MiniArch Snapshot Persistence Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build first-version full-world snapshot save/load for `MiniArch` using binary chunk/column persistence for unmanaged components.

**Architecture:** Keep `ComponentType` as the runtime hot-path id and add a separate persistence layer that serializes logical archetype/chunk content into a compact binary format. Loading should rebuild world state in batch instead of replaying structural changes through public `Add/Set/Remove` APIs.

**Tech Stack:** .NET 8, C#, xUnit, `dotnet` CLI.

---

## Task 1: Add failing snapshot persistence tests

**Files:**
- Create: `E:/godot/arch/miniArch-snapshot/tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`

**Step 1: Write the failing tests**

- Test that a world with unmanaged components can save and load with identical entity/component state.
- Test that multiple archetypes and multiple chunks round-trip correctly.
- Test that non-snapshot-safe components fail with a clear error.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch-snapshot/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldSnapshotTests`

Expected: fail because snapshot persistence types do not exist yet.

**Step 3: Commit**

- Do not implement production code in this task.

## Task 2: Add runtime persistence hooks

**Files:**
- Modify: `E:/godot/arch/miniArch-snapshot/src/MiniArch/Core/Chunk.cs`
- Modify: `E:/godot/arch/miniArch-snapshot/src/MiniArch/Core/Archetype.cs`
- Modify: `E:/godot/arch/miniArch-snapshot/src/MiniArch/Core/World.cs`

**Step 1: Write the minimal runtime-facing APIs needed by the tests**

- Add internal APIs for persistence-oriented archetype enumeration and creation.
- Add internal APIs to reserve/fill chunk rows without replaying structural changes.
- Add internal APIs to access entity/version/location reconstruction during load.

**Step 2: Run the snapshot tests**

Run: `dotnet test E:/godot/arch/miniArch-snapshot/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldSnapshotTests`

Expected: still fail because the persistence layer is not implemented yet, but fail later than before.

## Task 3: Implement snapshot schema registration and binary format

**Files:**
- Create: `E:/godot/arch/miniArch-snapshot/src/MiniArch/Persistence/ComponentSchema.cs`
- Create: `E:/godot/arch/miniArch-snapshot/src/MiniArch/Persistence/SnapshotComponentRegistry.cs`
- Create: `E:/godot/arch/miniArch-snapshot/src/MiniArch/Persistence/WorldSnapshot.cs`

**Step 1: Implement schema mapping**

- Map runtime component `Type` to stable persisted schema name.
- Reject unsupported component payloads.

**Step 2: Implement writer/reader**

- Write header, schema table, archetypes, chunks, entity blocks, and raw component column blocks.
- Read the file back and reconstruct the world through the runtime hooks.

**Step 3: Run the snapshot tests**

Run: `dotnet test E:/godot/arch/miniArch-snapshot/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldSnapshotTests`

Expected: snapshot tests pass.

## Task 4: Verify no core regressions

**Files:**
- Modify: `E:/godot/arch/miniArch-snapshot/.knowledge/kb-core-ecs.md`
- Modify: `E:/godot/arch/miniArch-snapshot/.knowledge/INDEX.md`
- Create: `E:/godot/arch/miniArch-snapshot/.knowledge/kb-snapshot-persistence.md`

**Step 1: Add knowledge page**

- Document runtime-vs-persistence boundaries, supported component rules, and load/save reconstruction rules.

**Step 2: Run the full test suite**

Run: `dotnet test E:/godot/arch/miniArch-snapshot/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal`

Expected: all tests pass.

**Step 3: Review diffs and summarize risks**

- Check that only intended files changed.
- Note remaining limitations such as same-version compatibility only and unmanaged-only persistence.
