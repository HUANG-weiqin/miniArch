# MiniArch Minimal ECS Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a minimal C# ECS that reproduces Arch’s archetype, chunk, query, and structural-change mechanics while omitting all nonessential features.

**Architecture:** The codebase will use a compact `World -> Archetype -> Chunk` data flow. Entities will be versioned ids, archetypes will be keyed by signatures, chunks will use SoA storage, and queries will cache matching archetypes before returning a chunk iterator. Structural changes will move entities between archetypes using cached transition edges when possible.

**Tech Stack:** .NET 8, C#, xUnit, `dotnet` CLI.

---

## Task 1: Create the project skeleton

**Files:**
- Create: `E:/godot/arch/miniArch/miniArch.sln`
- Create: `E:/godot/arch/miniArch/src/MiniArch/MiniArch.csproj`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj`
- Create: `E:/godot/arch/miniArch/src/MiniArch/README.md`

**Step 1: Create the failing workspace-level check**

- Add a simple solution-level test that fails until the project exists.
- Keep it minimal: the test should verify the solution can reference `MiniArch`.

**Step 2: Run the check to verify it fails**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal`

Expected: fail because the project and references do not exist yet.

**Step 3: Create the project skeleton**

- Create a solution.
- Create a class library for the ECS runtime.
- Create an xUnit test project.
- Wire the test project to reference the library.
- Target a modern .NET runtime consistently across both projects.

**Step 4: Run the check to verify it passes**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal`

Expected: restore/build/test succeeds, even if the tests are still empty.

**Step 5: Commit**

- Commit the skeleton separately so later ECS changes stay reviewable.

---

## Task 2: Add component registration and entity identity

**Files:**
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/ComponentRegistry.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/ComponentType.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/Entity.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ComponentRegistryTests.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/EntityTests.cs`

**Step 1: Write the failing tests**

- Test that the same component type always gets the same id.
- Test that different component types get different ids.
- Test that a recycled entity id becomes invalid once its version changes.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~ComponentRegistryTests`

Expected: fail because the types and registry do not exist yet.

**Step 3: Write the minimal implementation**

- Register component types with integer ids.
- Store reverse lookup by id.
- Define `Entity` as a readonly value type with `Id`, `Version`, and comparison helpers.
- Add version-based equality semantics.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~ComponentRegistryTests`

Expected: pass.

**Step 5: Commit**

- Commit the identity layer before storage work begins.

---

## Task 3: Add signature handling

**Files:**
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/Signature.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/SignatureTests.cs`

**Step 1: Write the failing tests**

- Test that signatures with the same component ids compare equal.
- Test that hash codes are stable for the same content.
- Test that add/remove operations produce the expected component sets.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~SignatureTests`

Expected: fail.

**Step 3: Write the minimal implementation**

- Represent the component set as a compact ordered collection.
- Cache the hash code.
- Provide `Add` and `Remove` helpers for component-set transitions.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~SignatureTests`

Expected: pass.

**Step 5: Commit**

- Keep signature logic isolated from storage so it is easy to reason about.

---

## Task 4: Build chunk storage

**Files:**
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/Chunk.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ChunkTests.cs`

**Step 1: Write the failing tests**

- Test that a chunk stores entities densely.
- Test that component columns stay aligned with entity rows.
- Test that removing a row swaps the last row into the gap.
- Test that setting a component mutates only the targeted row.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~ChunkTests`

Expected: fail.

**Step 3: Write the minimal implementation**

- Use SoA layout with one array per component type plus an entity array.
- Track `Count` and `Capacity`.
- Support `Add`, `Set`, `Get`, and swap-remove.
- Keep the API focused on archetype internals.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~ChunkTests`

Expected: pass.

**Step 5: Commit**

- This is the first point where the cache-friendly layout becomes visible.

---

## Task 5: Build archetype management

**Files:**
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/Archetype.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/ArchetypeEdges.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ArchetypeTests.cs`

**Step 1: Write the failing tests**

- Test that creating an archetype allocates an initial chunk.
- Test that adding entities fills the current chunk before allocating a new one.
- Test that removing an entity preserves dense packing.
- Test that add/remove transition edges are cached and reused.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~ArchetypeTests`

Expected: fail.

**Step 3: Write the minimal implementation**

- Key archetypes by signature.
- Hold a chunk list and total entity count.
- Create at least one chunk on construction.
- Add edge caches for add/remove transitions keyed by component id.
- Provide helpers to ensure chunk capacity and to move entities between archetypes.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~ArchetypeTests`

Expected: pass.

**Step 5: Commit**

- This step should preserve the Arch-style migration path.

---

## Task 6: Build world entity lifecycle

**Files:**
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/EntityInfo.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: Write the failing tests**

- Test that `create` returns a valid entity.
- Test that `destroy` recycles ids safely.
- Test that version mismatch makes stale entities invalid.
- Test that entity metadata points to the current archetype and chunk position.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldLifecycleTests`

Expected: fail.

**Step 3: Write the minimal implementation**

- Maintain `entity id -> metadata` storage.
- Recycle ids with version increments.
- Resolve entities by metadata, not by scanning.
- Keep world-level archetype lookup by signature hash.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldLifecycleTests`

Expected: pass.

**Step 5: Commit**

- Entity identity and metadata should be stable before structural changes are added.

---

## Task 7: Add structural changes

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/World.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Archetype.cs`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/Chunk.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`

**Step 1: Write the failing tests**

- Test that adding a component moves the entity to a new archetype.
- Test that removing a component moves the entity back to the smaller archetype.
- Test that `set` writes in place when the component already exists.
- Test that `set` behaves like `add` when the component does not exist.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldStructuralChangeTests`

Expected: fail.

**Step 3: Write the minimal implementation**

- Reuse cached archetype transition edges.
- Move entities between archetypes by inserting into the destination first, then removing from the source.
- Copy only the components that are shared between source and destination.
- Keep chunk removal as swap-remove.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~WorldStructuralChangeTests`

Expected: pass.

**Step 5: Commit**

- Structural changes are the highest-risk part of the ECS core, so keep them isolated.

---

## Task 8: Add query matching and chunk iteration

**Files:**
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/Query.cs`
- Create: `E:/godot/arch/miniArch/src/MiniArch/Core/QueryIterators.cs`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing tests**

- Test that a query returns only matching archetypes.
- Test that repeated queries reuse cached matches when the world has not changed.
- Test that chunk iteration visits each matching chunk exactly once.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: fail.

**Step 3: Write the minimal implementation**

- Build a query description object.
- Cache query instances in the world.
- Cache matching archetypes inside the query.
- Return a chunk iterator backed by a span or equivalent lightweight view.

**Step 4: Run tests to verify they pass**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~QueryTests`

Expected: pass.

**Step 5: Commit**

- This step should preserve the “filter archetypes first, iterate chunks second” rule.

---

## Task 9: Add integration coverage and cleanup

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/*`
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/*`
- Create: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/IntegrationTests.cs`
- Modify: `E:/godot/arch/miniArch/docs/plans/2026-04-11-miniarch-minimal-ecs-design.md`

**Step 1: Write the failing integration tests**

- Create an entity with multiple components.
- Add and remove components in sequence.
- Query for the resulting archetypes and chunks.
- Verify the final component values are correct after migration.

**Step 2: Run tests to verify they fail**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal --filter FullyQualifiedName~IntegrationTests`

Expected: fail if any earlier assumption is wrong.

**Step 3: Tighten the implementation**

- Remove accidental complexity.
- Simplify helper methods where the code became too indirect.
- Keep public surface area minimal.
- Ensure the docs match the final behavior.

**Step 4: Run the full test suite**

Run: `dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal`

Expected: all tests pass.

**Step 5: Commit**

- Finish with one final cleanup commit so the project is easy to continue from.

---

## Execution Notes

- Keep each task small and self-contained.
- Do not introduce events, jobs, source generators, or non-generic API surface during the first pass.
- Prefer explicit tests for memory movement and migration behavior over broad “it seems to work” tests.
- Preserve the Arch-inspired sequence:
  - signature identifies archetype
  - archetype owns chunks
  - chunks store contiguous columns
  - query filters archetypes
  - structural changes move entities between archetypes

