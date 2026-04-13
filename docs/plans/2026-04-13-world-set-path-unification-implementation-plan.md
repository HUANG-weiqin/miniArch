# World Set Path Unification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Unify the missing-component add/set migration path so generic and boxed `Set` follow the same archetype edge-cache logic, with regression coverage on replay entry points.

**Architecture:** Keep the existing public behavior unchanged and extract one small internal helper in `World.cs` for resolving the add-destination archetype. Replace the misleading edge-cache-specific test with public-behavior tests that cover direct `Set` and command-buffer replay.

**Tech Stack:** C#, xUnit, .NET 8

---

### Task 1: Add regression coverage for replay-backed set

**Files:**
- Modify: `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`

**Step 1: Add a regression test**
- Cover `CommandBuffer.Set(...)` when the component is missing but other components already exist.

**Step 2: Run targeted tests**
- Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj --filter WorldStructuralChangeTests`

**Step 3: Confirm replay/rewind semantics**
- Assert replay adds the missing component without dropping existing ones.
- Assert rewind removes only the replay-added component.

### Task 2: Unify add-destination resolution

**Files:**
- Modify: `src/MiniArch/Core/World.cs`

**Step 1: Extract helper**
- Add a small private helper that resolves the destination archetype for add/set of a missing component.

**Step 2: Update call sites**
- Use the helper from `Add<T>`, `Set<T>`, `AddBoxed(...)`, and `SetBoxed(...)`.

**Step 3: Keep behavior minimal**
- Do not change public API or data flow beyond deduplicating the path.

### Task 3: Verify the full test project

**Files:**
- Test: `tests/MiniArch.Tests/MiniArch.Tests.csproj`

**Step 1: Run full tests**
- Run: `dotnet test .\tests\MiniArch.Tests\MiniArch.Tests.csproj`

**Step 2: Check for regressions**
- Ensure the full test project passes with no failures.
