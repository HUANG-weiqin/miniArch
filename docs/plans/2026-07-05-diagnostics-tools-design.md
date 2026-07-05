# Diagnostics Tools for MiniArch.Diagnostics

## Goal

Add three diagnostic tools to `MiniArch.Diagnostics` to support lockstep ECS debugging:

1. **WorldValidator** — check world structural invariants (P0)
2. **EntityDump** — describe an entity's full state (P0/P1)
3. **WorldDigest** — per-domain checksum breakdown for rapid divergence narrowing (P1)

All three live in `src/MiniArch/Diagnostics/`, namespace `MiniArch.Diagnostics`, zero runtime overhead when unused.

---

## WorldValidator

### Purpose

Catch structural corruption at the point of mutation, before it manifests as lockstep divergence.

### API

```csharp
public static class WorldValidator
{
    public static ValidationResult Validate(World world);
}

public readonly struct ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<ValidationIssue> Issues { get; }
}

public readonly struct ValidationIssue
{
    public ValidationSeverity Severity { get; }      // Error | Warning
    public ValidationCategory Category { get; }       // EntitySlot | FreeList | Hierarchy | Archetype
    public ValidationCode Code { get; }               // e.g. OrphanedEntity, DuplicateId
    public string Description { get; }                // human-readable
}
```

### Checks performed

| Category | Check | Severity |
|----------|-------|----------|
| EntitySlot | Every occupied slot points to a valid archetype with a valid row | Error |
| EntitySlot | No two occupied slots reference the same (archetype, row) | Error |
| FreeList | No RecycledEntity in the free list is occupied (IsOccupied == true) | Error |
| FreeList | No RecycledEntity.Id duplicates in free list | Warning |
| Hierarchy | Every child's stored parent exists and has that child in its children | Error |
| Hierarchy | Every parent's children list entries point back to the parent | Error |
| Archetype | EntityCount == actual entities array length | Error |
| Archetype | No duplicate entity IDs across all archetypes | Error |
| SlotCount | EntitySlotCount >= occupiedCount + freeCount (reservations allowed) | Warning |

### Implementation notes

- Same-assembly access to `EntityRecord`, `Archetype`, `HierarchyTable`, `RecycledEntity`
- No mutations to the world
- Early-exit: first error can still produce a complete report (gather all issues)
- Deterministic: same world → same issue list order

---

## EntityDump

### Purpose

Make `WorldDiff` results actionable. When diff says "entity #42 differs," you need to see what's on it.

### API

```csharp
public static class EntityDump
{
    public static EntityReport Describe(World world, Entity entity);
}

public readonly struct EntityReport
{
    public bool IsAlive { get; }
    public int Id { get; }
    public int Version { get; }

    // Only set when IsAlive
    public ArchetypeInfo? Archetype { get; }
    public IReadOnlyList<ComponentInfo> Components { get; }
    public Entity? Parent { get; }
    public IReadOnlyList<Entity> Children { get; }
}

public readonly struct ArchetypeInfo
{
    public int EntityCount { get; }
    public IReadOnlyList<Type> ComponentTypes { get; }
}

public readonly struct ComponentInfo
{
    public Type Type { get; }
    public int SizeBytes { get; }
    // Only set when entity is alive and component is readable
    public byte[]? RawBytes { get; }
}
```

### What it does NOT do

- No struct field formatting (would require reflection or type-specific formatters)
- No UI rendering
- No mutation recording

### Formatting

`EntityReport` overrides `ToString()` for a human-readable multi-line dump:

```
Entity #42 (v3) — ALIVE
  Archetype: [Position, Velocity, Team] (12 entities)
  Components:
    Position (8 bytes): 1A 2B 3C 4D 5E 6F 7A 8B
    Velocity (8 bytes): ...
    Team    (4 bytes): ...
  Parent: #12 (v1)
  Children: #45 (v1), #48 (v2)
```

---

## WorldDigest

### Purpose

Narrow down a checksum mismatch before running the heavier `WorldDiff`. Answers: "Which domain diverged? Which component type? Which archetype?"

### API

```csharp
public static class WorldDigest
{
    public static WorldDigestResult Compute(World world);
}

public readonly struct WorldDigestResult
{
    public byte[] Total { get; }
    public byte[] Occupancy { get; }       // alive entity IDs + versions
    public byte[] FreeList { get; }         // free list (id + version) entries
    public byte[] Hierarchy { get; }       // parent-child relations
    public IReadOnlyDictionary<Type, byte[]> PerComponent { get; }  // one hash per component type
    public IReadOnlyDictionary<int, byte[]> PerArchetype { get; }   // one hash per archetype index
}
```

### Design

- Uses the same `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)` as existing checksums.
- Each domain feeds its own `IncrementalHash`, or one shared hash with domain-prefix separation.
- Per-component digest: for each component type, collect all rows' bytes in a deterministic order (by entity ID) and hash.
- Per-archetype digest: signature + entity IDs + all component data (same as existing `ComputeChecksum` but per-archetype instead of aggregated).
- Does NOT allocate intermediate lists except for per-component type data collection (similar to CanonicalChecksum).

### Comparison with WorldDiff

| | WorldDigest | WorldDiff |
|---|---|---|
| Cost | O(N) checksum, ~same as `Checksum()` | O(N) full comparison with byte-level diff |
| Output | Hashes per domain | Entity-level diff list |
| Use case | "Is it hierarchy, Position, or free list?" | "Exactly which entities and values differ?" |
| Run first? | Yes (fast scan) | No (use after digest pinpoints domain) |

---

## File structure

```
src/MiniArch/Diagnostics/
├── WorldDiff.cs             # already done
├── WorldValidator.cs         # new
├── WorldValidatorResult.cs   # new (types: ValidationResult, ValidationIssue, enums)
├── EntityDump.cs             # new
├── EntityDumpResult.cs       # new (types: EntityReport, ArchetypeInfo, ComponentInfo)
├── WorldDigest.cs            # new
└── WorldDigestResult.cs      # new (type: WorldDigestResult)
```

Tests go in `tests/MiniArch.Tests/Diagnostics/`.

---

## Order

1. WorldValidator (P0) — 2 files + tests
2. EntityDump (P0/P1) — 2 files + tests  
3. WorldDigest (P1) — 2 files + tests
