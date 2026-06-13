# MiniArch Query Description Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a reusable `MiniArch.Core.QueryDescription` and `World.Query(in QueryDescription)` entrypoint, with tests proving semantic equivalence, cache reuse, cross-world reuse, and no extra steady-state GC.

**Architecture:** Keep `QueryDescription` world-agnostic by storing `Type` sets, translate it to the existing world-local `QueryFilter` inside `World`, and reuse the current cached `Query` pipeline unchanged. Use TDD for each slice so semantics and GC constraints are locked before implementation.

**Tech Stack:** C#, .NET, xUnit, MiniArch.Core, MiniArch.Tests

---

### Task 1: Add failing tests for `QueryDescription` semantics

**Files:**
- Modify: `tests/MiniArch.Tests/Core/QueryFilterTests.cs`

**Step 1: Write the failing test**

Add tests that express:

```csharp
[Fact]
public void Query_description_builds_expected_required_excluded_and_any_sets()
{
    var description = new QueryDescription()
        .With<Position>()
        .Without<Velocity>()
        .WithAny<TagA>()
        .Or<TagB>();

    Assert.Contains(typeof(Position), description.RequiredTypes);
    Assert.Contains(typeof(Velocity), description.ExcludedTypes);
    Assert.Contains(typeof(TagA), description.AnyTypes);
    Assert.Contains(typeof(TagB), description.AnyTypes);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryFilterTests`
Expected: FAIL because `QueryDescription` and related members do not exist yet.

**Step 3: Write minimal implementation**

Create `src/MiniArch/Core/QueryDescription.cs` with immutable `Type`-based sets and fluent methods.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryFilterTests`
Expected: PASS for the new semantics tests.

### Task 2: Add failing tests for world query equivalence and cache reuse

**Files:**
- Modify: `tests/MiniArch.Tests/Core/QueryFilterTests.cs`
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`
- Modify: `src/MiniArch/Core/World.cs`

**Step 1: Write the failing test**

Add tests that express:

```csharp
[Fact]
public void Description_query_entrypoint_matches_generic_and_chain_queries()
{
    var world = new World();
    var description = new QueryDescription().With<Position>();

    Assert.Same(world.Query<Position>(), world.Query(in description));
    Assert.Same(world.Query().With<Position>().Build(), world.Query(in description));
}
```

and:

```csharp
[Fact]
public void Repeated_queries_with_same_description_reuse_cached_query()
{
    var world = new World();
    var description = new QueryDescription().With<Position>();

    Assert.Same(world.Query(in description), world.Query(in description));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests|QueryFilterTests`
Expected: FAIL because `World.Query(in QueryDescription)` does not exist yet.

**Step 3: Write minimal implementation**

Add `World.Query(in QueryDescription description)` and translate the description to `QueryFilter` before calling `GetOrCreateQuery(filter)`.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests|QueryFilterTests`
Expected: PASS.

### Task 3: Add failing test for cross-world reuse

**Files:**
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing test**

Add a test that uses one `QueryDescription` instance against two different `World` instances and confirms each world returns the correct cached query and matching entities.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests`
Expected: FAIL if the description is not truly world-agnostic.

**Step 3: Write minimal implementation**

Adjust description translation or equality only if needed; do not broaden API surface.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests`
Expected: PASS.

### Task 4: Add failing allocation smoke test for warmed repeated usage

**Files:**
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: Write the failing test**

Add an allocation smoke test that warms the query, then measures repeated `world.Query(in description)` plus chunk enumeration with `GC.GetAllocatedBytesForCurrentThread()`.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests`
Expected: FAIL if the new path allocates on steady-state usage.

**Step 3: Write minimal implementation**

Remove any avoidable allocations in the description translation path while preserving correctness.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests`
Expected: PASS with zero additional steady-state allocation in the measured loop.

### Task 5: Run focused and broader verification

**Files:**
- Modify: `src/MiniArch/Core/QueryDescription.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`
- Modify: `tests/MiniArch.Tests/Core/QueryFilterTests.cs`

**Step 1: Run focused query tests**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter QueryTests|QueryFilterTests`
Expected: PASS.

**Step 2: Run broader verification**

Run: `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1`
Expected: build and test pass, or any unrelated existing blocker is clearly identified.

**Step 3: Review docs and knowledge updates**

If the final design reveals reusable project knowledge, update `.knowledge/INDEX.md` and the relevant `kb-*.md` page.

**Step 4: Commit**

Only if explicitly requested by the user.
