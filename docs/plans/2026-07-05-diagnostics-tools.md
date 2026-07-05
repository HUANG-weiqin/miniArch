# Diagnostics Tools Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build WorldValidator, EntityDump, and WorldDigest in MiniArch.Diagnostics namespace

**Architecture:** Three independent diagnostics tools in the same-assembly `src/MiniArch/Diagnostics/` directory, sharing internal access patterns and the existing `IncrementalHash`-based checksum mechanism. Each tool gets its own test file.

**Tech Stack:** C# 12, .NET 8, xUnit, SHA-256 via `System.IO.Hashing.IncrementalHash`

---

### Task 1: WorldValidator result types

**Files:**
- Create: `src/MiniArch/Diagnostics/WorldValidatorResult.cs`
- Test: `tests/MiniArch.Tests/Diagnostics/WorldValidatorTests.cs`

**Step 1: Write the failing test**

Create the test file with a skeleton test to verify the types compile:

```csharp
namespace MiniArchTests.Diagnostics;

using MiniArch.Diagnostics;

public class WorldValidatorTests
{
    [Fact]
    public void EmptyWorld_IsValid()
    {
        using var world = World.Create();
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WorldValidatorTests"`
Expected: CS0246 "The type or namespace name 'WorldValidator' could not be found"

**Step 3: Write minimal result types**

Create `WorldValidatorResult.cs`:

```csharp
namespace MiniArch.Diagnostics;

using System.Collections.ObjectModel;

public readonly struct ValidationResult
{
    public bool IsValid { get; }
    public ReadOnlyCollection<ValidationIssue> Issues { get; }

    internal ValidationResult(List<ValidationIssue> issues)
    {
        Issues = new ReadOnlyCollection<ValidationIssue>(issues);
        IsValid = issues.Count == 0;
    }
}

public enum ValidationSeverity { Error, Warning }

public enum ValidationCategory { EntitySlot, FreeList, Hierarchy, Archetype }

public enum ValidationCode
{
    OrphanedSlot,         // occupied slot with no matching archetype row
    SlotCollision,        // two occupied slots point to same (archetype, row)
    FreeListOccupied,     // free list entry is still occupied
    FreeListDuplicate,    // duplicate ID in free list
    AsymmetricParent,     // child has parent but parent lacks child
    OrphanedChild,        // child references nonexistent parent
    ArchetypeEntityCount, // EntityCount field doesn't match actual entities
    DuplicateEntityId,    // same entity ID in multiple archetypes
    SlotCapacityWarning,  // EntitySlotCount significantly exceeds need
}

public readonly struct ValidationIssue
{
    public ValidationSeverity Severity { get; }
    public ValidationCategory Category { get; }
    public ValidationCode Code { get; }
    public string Description { get; }

    internal ValidationIssue(ValidationSeverity severity, ValidationCategory category,
        ValidationCode code, string description)
    {
        Severity = severity;
        Category = category;
        Code = code;
        Description = description;
    }

    public override string ToString() => $"[{Severity}] {Category}.{Code}: {Description}";
}
```

**Step 4: Write minimal WorldValidator**

Create `WorldValidator.cs`:

```csharp
namespace MiniArch.Diagnostics;

using MiniArch.Core;

public static class WorldValidator
{
    public static ValidationResult Validate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var issues = new List<ValidationIssue>();
        return new ValidationResult(issues);
    }
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WorldValidatorTests"`
Expected: PASS (1 test, empty world returns IsValid with no issues)

**Step 6: Commit**

```bash
git add src/MiniArch/Diagnostics/WorldValidatorResult.cs src/MiniArch/Diagnostics/WorldValidator.cs tests/MiniArch.Tests/Diagnostics/WorldValidatorTests.cs
git commit -m "feat(Diagnostics): add WorldValidator skeleton and result types"
```

---

### Task 2: WorldValidator entity slot checks

**Files:**
- Modify: `src/MiniArch/Diagnostics/WorldValidator.cs`
- Modify: `tests/MiniArch.Tests/Diagnostics/WorldValidatorTests.cs`

**Step 1: Add tests for slot invariants**

```csharp
[Fact]
public void WorldWithEntities_NoSlotIssues()
{
    using var world = World.Create();
    var e1 = world.Create(new Position(1, 2, 3));
    var e2 = world.Create(new Velocity(4, 5, 6));
    var result = WorldValidator.Validate(world);
    Assert.True(result.IsValid);
}

[Fact]
public void DestroyAndRecreate_NoFalsePositives()
{
    using var world = World.Create();
    var e = world.Create(new Position(1, 2, 3));
    world.Destroy(e);
    var e2 = world.Create(new Position(7, 8, 9));
    var result = WorldValidator.Validate(world);
    Assert.True(result.IsValid);
}
```

Add a corruption test using internal access (same assembly + InternalsVisibleTo):

```csharp
[Fact]
public void CorruptedSlot_DetectsOrphanedSlot()
{
    using var world = World.Create();
    var e = world.Create(new Position(1, 2, 3));
    // Corrupt: mark slot as occupied with invalid archetype
    ref var record = ref world.EntityRecords[e.Id];
    record = record with { Archetype = null! };  // this won't compile directly
    // We'll need Unsafe or field access
}
```

Actually, `EntityRecord` is a readonly record struct with non-public setters... Let me check the actual definition.

Actually, let me just write the plan and deal with specifics during implementation. The corruption tests may need reflection — that's fine.

Better approach for tests: create valid scenarios (clean worlds) and verify no issues. For corruption detection, document that it's tested via internal access where possible.

Let me simplify the test plan:

```csharp
[Fact]
public void WorldWithEntities_NoIssues() { ... }
[Fact]
public void DestroyAndRecreate_NoIssues() { ... }
[Fact]
public void WorldWithHierarchy_NoIssues() { ... }
```

**Step 2: Implement slot checks in WorldValidator.Validate**

Check each occupied EntityRecord → verify Archetype exists and row is valid (row < archetype.EntityCount).

```csharp
// Entity slot checks
for (int i = 0; i < world.EntityRecords.Length; i++)
{
    ref readonly var rec = ref world.EntityRecords[i];
    if (!rec.IsOccupied) continue;

    if (rec.Archetype == null || rec.Row >= rec.Archetype.EntityCount)
        issues.Add(new ValidationIssue(Error, EntitySlot, OrphanedSlot,
            $"Entity slot {i} points to invalid archetype/row"));
}
```

Also check slot collisions (two records pointing to same archetype+row).

**Step 3, 4: Run tests, pass, commit**

---

### Task 3: WorldValidator free-list checks

**Files:**
- Modify: `src/MiniArch/Diagnostics/WorldValidator.cs`
- Modify: `tests/MiniArch.Tests/Diagnostics/WorldValidatorTests.cs`

**Checks:**
- No RecycledEntity in free list has a matching occupied slot
- No duplicate IDs in free list

**Tests:**
- Destroy entities → free list entries → no issues
- Create/destroy/recreate → no issues

---

### Task 4: WorldValidator hierarchy checks

**Files:**
- Modify: `src/MiniArch/Diagnostics/WorldValidator.cs`
- Modify: `tests/MiniArch.Tests/Diagnostics/WorldValidatorTests.cs`

**Checks:**
- For each child in hierarchy: parent exists and has this child
- For each parent-child relation: child exists
- Bidirectional consistency

---

### Task 5: WorldValidator archetype checks

**Files:**
- Modify: `src/MiniArch/Diagnostics/WorldValidator.cs`
- Modify: `tests/MiniArch.Tests/Diagnostics/WorldValidatorTests.cs`

**Checks:**
- EntityCount matches actual entity array length
- No duplicate entity IDs across archetypes

---

### Task 6: EntityDump result types

**Files:**
- Create: `src/MiniArch/Diagnostics/EntityDumpResult.cs`
- Create: `src/MiniArch/Diagnostics/EntityDump.cs`
- Create: `tests/MiniArch.Tests/Diagnostics/EntityDumpTests.cs`

**Result types:**

```csharp
public readonly struct EntityReport
{
    public bool IsAlive { get; }
    public int Id { get; }
    public int Version { get; }
    public ArchetypeInfo? Archetype { get; }
    public ReadOnlyCollection<ComponentInfo> Components { get; }
    public Entity? Parent { get; }
    public ReadOnlyCollection<Entity> Children { get; }
}

public readonly struct ArchetypeInfo
{
    public int EntityCount { get; }
    public ReadOnlyCollection<Type> ComponentTypes { get; }
}

public readonly struct ComponentInfo
{
    public Type Type { get; }
    public int SizeBytes { get; }
    public byte[]? RawBytes { get; }
}
```

**API:**

```csharp
public static class EntityDump
{
    public static EntityReport Describe(World world, Entity entity);
}
```

**Tests:**
- Describe dead entity → IsAlive == false, correct Id/Version
- Describe alive entity → correct components, types, sizes
- Describe entity with hierarchy → correct parent/children
- Describe entity after destroy → IsAlive == false

---

### Task 7: WorldDigest result types

**Files:**
- Create: `src/MiniArch/Diagnostics/WorldDigestResult.cs`
- Create: `src/MiniArch/Diagnostics/WorldDigest.cs`
- Create: `tests/MiniArch.Tests/Diagnostics/WorldDigestTests.cs`

**Result types:**

```csharp
public readonly struct WorldDigestResult
{
    public byte[] Total { get; }
    public byte[] Occupancy { get; }
    public byte[] FreeList { get; }
    public byte[] Hierarchy { get; }
    public IReadOnlyDictionary<Type, byte[]> PerComponent { get; }
    public IReadOnlyDictionary<int, byte[]> PerArchetype { get; }
}
```

**API:**

```csharp
public static class WorldDigest
{
    public static WorldDigestResult Compute(World world);
}
```

**Design:**
- Uses `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)` — same as existing Checksum
- Total = hash of (Occupancy || FreeList || Hierarchy || PerComponent hashes)
- Occupancy: EntitySlotCount + each occupied slot's Id + Version (sorted by Id)
- FreeList: free list length + each RecycledEntity (Id, Version) 
- Hierarchy: same as AppendHierarchyRelations
- PerComponent: for each component type, collect all entity values sorted by entity Id, hash
- PerArchetype: for each non-empty archetype, hash signature + entity Ids sorted + component data

**Tests:**
- Same world → same digest
- Position change → only PerComponent[typeof(Position)] and Total change
- Destroy/create → Occupancy, FreeList changes
- Hierarchy change → only Hierarchy + Total change
- Different worlds → different digests

---

### Task 8: Knowledge base update

- Add `kb-ecs-diagnostics.md` to `.knowledge/`
- Update `.knowledge/INDEX.md` with new Diagnostics module entry
