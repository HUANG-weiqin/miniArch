# MiniArch Hierarchy Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add runtime-owned parent-child hierarchy support to MiniArch with cascade destroy, parent/children queries, and snapshot round-trip restoration.

**Architecture:** Keep hierarchy state out of ECS components and store it as `World`-owned side tables keyed by `Entity.Id` and `Entity.Version`. Extend `World.Destroy` to expand parent destroys into a child-first subtree destroy, and extend `WorldSnapshot` to persist live hierarchy links separately from archetype/component data.

**Tech Stack:** C#, .NET, xUnit, MiniArch.Core snapshot binary format

---

### Task 1: Lock the public behavior with failing lifecycle tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: Write the failing test**

Add tests for:
- linking a child and reading back its parent
- reading a parent's direct children as a list
- destroying a parent and verifying all descendants are no longer alive
- reusing a destroyed slot without inheriting the old relationship

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldLifecycleTests"`
Expected: FAIL because hierarchy APIs do not exist yet.

**Step 3: Write minimal implementation**

Add the minimal `World` APIs and internal hooks required for those tests to compile and exercise the hierarchy contract.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldLifecycleTests"`
Expected: PASS.

### Task 2: Lock snapshot round-trip behavior with failing persistence tests

**Files:**
- Modify: `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`

**Step 1: Write the failing test**

Add tests for:
- save/load preserving parent lookup and children list
- save/load preserving cascade destroy behavior after restore

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldSnapshotTests"`
Expected: FAIL because hierarchy data is not persisted yet.

**Step 3: Write minimal implementation**

Extend snapshot format and load/save flow to persist hierarchy relations for live entities only.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldSnapshotTests"`
Expected: PASS.

### Task 3: Implement the runtime-owned hierarchy table in `World`

**Files:**
- Create: `src/MiniArch/Core/HierarchyTable.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Ecs/World.cs`

**Step 1: Write the failing test**

Reuse the tests from Task 1 and keep them red.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldLifecycleTests"`
Expected: FAIL.

**Step 3: Write minimal implementation**

Implement:
- single-parent runtime table keyed by entity id/version
- `AddChild`, `RemoveChild`, `TryGetParent`, `GetChildren`
- cycle rejection and stale-entity validation
- child-first subtree collection and internal bulk destroy path
- cleanup on destroy and slot reuse safety

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldLifecycleTests"`
Expected: PASS.

### Task 4: Integrate hierarchy with snapshot save/load

**Files:**
- Modify: `src/MiniArch/Core/WorldSnapshot.cs`
- Modify: `src/MiniArch/Core/World.cs`

**Step 1: Write the failing test**

Reuse the tests from Task 2 and keep them red.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldSnapshotTests"`
Expected: FAIL.

**Step 3: Write minimal implementation**

Persist hierarchy records after component/archetype data, then restore them after entity slot versions and archetype chunks are rebuilt.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldSnapshotTests"`
Expected: PASS.

### Task 5: Update project knowledge and run final verification

**Files:**
- Modify: `.knowledge/INDEX.md`
- Create or Modify: `.knowledge/kb-hierarchy-runtime.md`

**Step 1: Write the knowledge update**

Document the hierarchy runtime design, destroy semantics, snapshot behavior, and extension points using the repo template.

**Step 2: Run verification**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj`
Expected: PASS.

**Step 3: Confirm knowledge index accuracy**

Ensure `.knowledge/INDEX.md` links the new page and the `updated` date is current.
