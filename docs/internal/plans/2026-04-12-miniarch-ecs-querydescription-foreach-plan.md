# MiniArch Ecs QueryDescription Foreach Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a user-facing `MiniArch.Ecs.QueryDescription` plus a `foreach`-able `MiniArch.Ecs.Query` for entity traversal, while keeping `MiniArch.Core` as the advanced backend.

**Architecture:** Reuse the existing world-agnostic `MiniArch.Core.QueryDescription` and `MiniArch.Core.Query` cache. The new user-layer types should be thin facades: `MiniArch.Ecs.QueryDescription` mirrors the fluent builder, `MiniArch.Ecs.World.Query(in QueryDescription)` materializes a user-layer query, and `MiniArch.Ecs.Query` enumerates matching `Entity` values by walking the matched chunk snapshot.

**Tech Stack:** C#, .NET, xUnit, MiniArch.Core, MiniArch.Ecs

---

### Task 1: Add failing user API tests

**Files:**
- Modify: `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`

**Step 1: Write the failing test**

Add tests that express:

```csharp
[Fact]
public void Description_based_query_can_be_enumerated_directly_with_foreach()
{
    var world = new World();
    var description = new QueryDescription().With<Position>().Without<Velocity>();
    var entity = world.Create(new Position(1, 2));

    var seen = new List<Entity>();
    foreach (var item in world.Query(in description))
    {
        seen.Add(item);
    }

    Assert.Equal(new[] { entity }, seen);
}
```

Also add a small compatibility test that verifies `QueryDescription` is available from `MiniArch.Ecs` and supports the fluent builder.

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter UserQueryTests`
Expected: FAIL because `MiniArch.Ecs.QueryDescription` and `World.Query(in QueryDescription)` do not exist yet.

**Step 3: Write minimal implementation**

Create the user-layer query description wrapper, the user-layer non-generic query facade, and the `World.Query(in QueryDescription)` overload.

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter UserQueryTests`
Expected: PASS for the new user API tests.

### Task 2: Update user-facing docs

**Files:**
- Modify: `src/MiniArch/README.md`
- Modify: `.knowledge/kb-user-api-layering.md`
- Modify: `.knowledge/INDEX.md`

**Step 1: Write the documentation change**

Document the new ordinary-user API shape:

```csharp
var description = new QueryDescription().With<Position>().Without<Sleeping>();
foreach (var entity in world.Query(in description))
{
    // ...
}
```

Call out that `MiniArch.Ecs.QueryDescription` is the ordinary-layer entry and `MiniArch.Core.QueryDescription` remains the advanced backend type.

**Step 2: Run a quick sanity check**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter UserQueryTests`
Expected: PASS.

**Step 3: Keep knowledge index accurate**

If the user API boundary changes, update the relevant knowledge page and index entry so future readers land on the correct layer description.

### Task 3: Run full verification

**Files:**
- Modify: `src/MiniArch/Ecs/QueryDescription.cs`
- Modify: `src/MiniArch/Ecs/Query.cs`
- Modify: `src/MiniArch/Ecs/World.cs`
- Modify: `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`
- Modify: `src/MiniArch/README.md`
- Modify: `.knowledge/kb-user-api-layering.md`
- Modify: `.knowledge/INDEX.md`

**Step 1: Run focused user API tests**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter UserQueryTests`
Expected: PASS.

**Step 2: Run broader verification**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj`
Expected: PASS, or any unrelated failures are clearly identified.

**Step 3: Verify docs stayed consistent**

Check that README examples, knowledge pages, and the index all describe the same public boundary.
