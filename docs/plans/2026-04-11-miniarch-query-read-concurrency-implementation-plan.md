# MiniArch Query Read Concurrency Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `Query` safe for lock-free concurrent read-only use across multiple threads when the `World` is not being mutated.

**Architecture:** Replace shared mutable query-visible collections with copy-on-write snapshots. `World` publishes archetype and query-cache snapshots, and `Query` publishes immutable matched-archetype snapshots that enumerators capture once.

**Tech Stack:** .NET 8, C#, xUnit

---

### Task 1: Add concurrency regression tests

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing tests**

- Add a test that enumerates the same `Query` from many tasks at once and asserts each task sees the same chunk count.
- Add a test that materializes and enumerates equivalent queries from many tasks at once and asserts there are no exceptions and the returned counts are stable.

**Step 2: Run test to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: fail because the current shared mutable query cache is not safe for concurrent use.

**Step 3: Write minimal implementation**

- Keep the tests narrow and deterministic.
- Use repeated task fan-out instead of timing-based sleeps.

**Step 4: Run test to verify it passes**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: pass.

### Task 2: Convert query matching to immutable snapshots

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Query.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/QueryIterators.cs`

**Step 1: Write the failing tests**

- Reuse the new concurrency tests as the red phase for snapshot publication.

**Step 2: Run test to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: fail before the refresh path is snapshot-based.

**Step 3: Write minimal implementation**

- Replace the shared mutable `List<Archetype>` cache with a published `Archetype[]`.
- Build refreshed matches into local storage and publish only after the snapshot is complete.
- Make the enumerator capture one snapshot reference when enumeration starts.

**Step 4: Run test to verify it passes**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: pass.

### Task 3: Convert world query-visible state to immutable snapshots

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`

**Step 1: Write the failing tests**

- Reuse the new concurrent query creation test as the red phase for cache publication.

**Step 2: Run test to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: fail before `World` publishes immutable archetype/query snapshots.

**Step 3: Write minimal implementation**

- Publish an archetype snapshot array for query readers.
- Replace direct query-cache mutation with copy-on-write publication.
- Preserve the existing public API and refresh semantics.

**Step 4: Run test to verify it passes**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: pass.

### Task 4: Run full verification

**Files:**
- No code changes required unless verification finds regressions.

**Step 1: Run focused core tests**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~Query`

Expected: pass.

**Step 2: Run full test suite**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal`

Expected: pass.

### Task 5: Update project knowledge

**Files:**
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-core-ecs.md`
- Modify: `E:/godot/arch/miniArch/.knowledge/kb-test-workflow.md`

**Step 1: Record the contract**

- Document that query supports lock-free concurrent read-only usage only when world writes are not concurrent.
- Document the copy-on-write snapshot model for query-visible state.

**Step 2: Verify the knowledge index stays accurate**

Run: `Get-Content E:/godot/arch/miniArch/.knowledge/INDEX.md`

Expected: no index update required unless a new page is introduced.
