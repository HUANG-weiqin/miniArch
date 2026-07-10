# DestroyMany / Destroy(QueryDescription) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `DestroyMany(ReadOnlySpan<Entity>)` and `Destroy(QueryDescription)` to World, with internal two-phase bulk-remove optimization.

**Architecture:** Both APIs share `DestroyEntitiesCore` — Phase 1 cascades entities with children, Phase 2 bulk-removes childless entities by archetype. Uses `_destroyVisitedGen`-based dead-set for O(1) dedup with zero allocation.

**Tech Stack:** C# 12, .NET 8, MiniArch ECS

---

### Task 1: KillEntityRecord + DeadSet infrastructure

**Files:**
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs`

**Context:** `DestroySingle` currently inlines version bump, hierarchy cleanup, free-list push. We need this as a standalone helper for the bulk path.

**Step 1: Extract `KillEntityRecord` from `DestroySingle`**

Current `DestroySingle` (lines 205-228):
```csharp
private void DestroySingle(Entity entity)
{
    var info = RequireLocation(entity);   // bounds + version check
    var nextVersion = NextEntityVersion(entity);
    var arch = info.Archetype!;

    arch.RemoveAt(info.RowIndex, out var movedEntity);
    if (_hierarchy.HasAnyRelations(entity))
        _hierarchy.RemoveDestroyed(entity);

    ref var record = ref _records[entity.Id];
    record = default;
    record.Version = nextVersion;
    PushFreeIdUnsafe(entity.Id, nextVersion);

    if (movedEntity.IsValid)
    {
        ref var movedRecord = ref _records[movedEntity.Id];
        movedRecord.Archetype = info.Archetype;
        movedRecord.RowIndex = info.RowIndex;
    }
}
```

Extract:
```csharp
// Pure record cleanup — no archetype RemoveAt, no movedEntity update.
// Caller must have already removed the entity from its archetype storage.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void KillEntityRecord(Entity entity)
{
    var nextVersion = NextEntityVersion(entity);
    if (_hierarchy.HasAnyRelations(entity))
        _hierarchy.RemoveDestroyed(entity);
    ref var record = ref _records[entity.Id];
    record = default;
    record.Version = nextVersion;
    PushFreeIdUnsafe(entity.Id, nextVersion);
}
```

Then refactor `DestroySingle` to call `KillEntityRecord` after the `RemoveAt`:
```csharp
private void DestroySingle(Entity entity)
{
    var info = RequireLocation(entity);
    var arch = info.Archetype!;
    arch.RemoveAt(info.RowIndex, out var movedEntity);

    KillEntityRecord(entity);

    if (movedEntity.IsValid)
    {
        ref var movedRecord = ref _records[movedEntity.Id];
        movedRecord.Archetype = info.Archetype;
        movedRecord.RowIndex = info.RowIndex;
    }
}
```

**Step 2: Add DeadSet helpers**

Add alongside `_destroyVisitedGen` / `_destroyCurrentGen`:
```csharp
// Checks if dead-set marks entity as already destroyed.
// Uses generation counter to skip per-call zeroing.
// Returns false when id out of range (same semantics as "not dead").
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private bool IsDead(int entityId) =>
    (uint)entityId < (uint)_destroyVisitedGen.Length
    && _destroyVisitedGen[entityId] == _destroyCurrentGen;

// Marks entity as destroyed in the dead set.
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void MarkDead(int entityId)
{
    if ((uint)entityId < (uint)_destroyVisitedGen.Length)
        _destroyVisitedGen[entityId] = _destroyCurrentGen;
}
```

Also add a helper to cover the existing `_destroyCurrentGen` overflow path already in `Destroy`:
```csharp
// Ensure gen counter won't overflow; called at entry of DestroyMany/Destroy(query).
private void EnsureDestroyGenCapacity(int slotCount)
{
    if (_destroyVisitedGen.Length < slotCount)
    {
        var newLen = Math.Max(slotCount, _destroyVisitedGen.Length == 0 ? 16 : _destroyVisitedGen.Length * 2);
        Array.Resize(ref _destroyVisitedGen, newLen);
    }
}
```

**Step 3: Run existing tests**

Run: `dotnet test -c Release --filter "WorldLifecycleTests"` — all existing DestroySingle tests must still pass after the refactor.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/World.EntityLifecycle.cs
git commit -m "refactor: extract KillEntityRecord helper, add DeadSet infrastructure"
```

---

### Task 2: DestroyMany(ReadOnlySpan<Entity>)

**Files:**
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs`

**Step 1: Write failing tests (see Task 4)**

**Step 2: Implement `DestroyMany`**

Add to `World.EntityLifecycle.cs`:

```csharp
/// <summary>
/// Destroys multiple entities in a single batch. Dead entities are silently skipped.
/// Entities with children cascade-destroy their subtree (post-order), then childless
/// entities are bulk-removed per archetype. The internal two-phase algorithm minimizes
/// per-entity structural overhead: Phase 1 cascades, Phase 2 groups by archetype
/// and removes via either ResetCount (full archetype) or descending swap-remove.
/// </summary>
public void DestroyMany(ReadOnlySpan<Entity> entities)
{
    AssertNotDisposed();
    if (entities.Length == 0) return;

    // ── Phase 1: Cascade entities with children ──
    // Use the destroy-visited generation for O(1) dead-set dedup.
    // Bump gen (handling overflow) so IsDead/MarkDead from a prior Destroy
    // or DestroyMany call doesn't pollute this batch.
    if (++_destroyCurrentGen == 0)
    {
        Array.Clear(_destroyVisitedGen);
        _destroyCurrentGen = 1;
    }
    EnsureDestroyGenCapacity(_entitySlotCount);

    // Temporary storage for childless entities: (Entity, Archetype, RowIndex).
    // Use ArrayPool to avoid per-call allocation on the hot path.
    var rented = ArrayPool<(Entity E, Archetype A, int Row)>.Shared.Rent(entities.Length);
    var childlessCount = 0;

    try
    {
        foreach (var entity in entities)
        {
            if (!IsAlive(entity)) continue;
            if (IsDead(entity.Id)) continue;           // already killed by cascade

            if (_hierarchy.HasChildren(this, entity))
            {
                // Cascade destroy (existing logic) — kills entity + subtree
                DestroyImpl(entity);  // NOTE: we need to understand interaction

                // Mark all cascade-killed entities in dead set.
                // _destroyOrderScratch is populated by DestroyImpl / CollectDestroySubtree.
                for (var i = 0; i < _destroyOrderScratch.Count; i++)
                    MarkDead(_destroyOrderScratch[i].Id);
            }
            else
            {
                // Record for Phase 2 bulk removal
                var info = RequireLocation(entity);
                rented[childlessCount++] = (entity, info.Archetype!, info.RowIndex);
                MarkDead(entity.Id);
            }
        }

        // ── Phase 2: Bulk-remove childless entities, grouped by archetype ──
        if (childlessCount == 0) return;

        // Sort by archetype (pointer), then by row index descending.
        var span = rented.AsSpan(0, childlessCount);
        span.Sort(static (a, b) =>
        {
            // Sort by Archetype pointer first (group by archetype).
            // Use RuntimeHelpers.GetHashCode to compare references,
            // but since Archetype is a sealed class, simple reference
            // comparison via Unsafe.As<Archetype, nint> works.
            // To avoid unsafe, compare by pair: (ReferenceEquals, Row).
            // Actually, the simplest: sort by (Archetype pointer, -RowIndex).
            var archCmp = ReferenceEquals(a.A, b.A) ? 0
                : (nint)a.A < (nint)b.A ? -1 : 1;
            if (archCmp != 0) return archCmp;
            return b.Row.CompareTo(a.Row); // descending row
        });

        // Process each archetype group.
        var groupStart = 0;
        while (groupStart < childlessCount)
        {
            var arch = span[groupStart].A;
            var groupEnd = groupStart + 1;
            while (groupEnd < childlessCount && ReferenceEquals(span[groupEnd].A, arch))
                groupEnd++;

            var count = groupEnd - groupStart;

            // Case A: full archetype clear → ResetCount()
            if (count == arch.EntityCount)
            {
                for (var i = groupStart; i < groupEnd; i++)
                    KillEntityRecord(span[i].E);
                arch.ResetCount();
            }
            // Case B: partial clear → descending swap-remove
            else
            {
                // Rows are already sorted descending within each group
                // because we sorted by (-RowIndex) above.
                for (var i = groupStart; i < groupEnd; i++)
                {
                    var row = span[i].Row;
                    arch.RemoveAt(row, out var movedEntity);
                    KillEntityRecord(span[i].E);
                    if (movedEntity.IsValid)
                    {
                        ref var movedRecord = ref _records[movedEntity.Id];
                        movedRecord.Archetype = arch;
                        movedRecord.RowIndex = row;
                    }
                }
            }

            groupStart = groupEnd;
        }
    }
    finally
    {
        ArrayPool<(Entity, Archetype, int)>.Shared.Return(rented);
    }
}
```

**Key design points:**
- Uses `ArrayPool` for the childless buffer — zero per-call allocation in steady state.
- Phase 1 reuses `_destroyVisitedGen` pattern — exactly like existing `Destroy`.
- The sort by `(Archetype pointer, -RowIndex)` uses `nint` pointer comparison for cheap grouping. Archetype objects live for the World's lifetime, so pointer ordering is stable.
- Phase 2 Case A (`ResetCount`) avoids any per-entity `RemoveAt` — O(1) per archetype.

**Step 3: Run tests**

Run: `dotnet test -c Release --filter "DestroyMany"` — all pass.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/World.EntityLifecycle.cs
git commit -m "feat: implement DestroyMany(ReadOnlySpan<Entity>)"
```

---

### Task 3: Destroy(QueryDescription)

**Files:**
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs`

**Step 1: Write failing tests (see Task 4)**

**Step 2: Implement `Destroy(QueryDescription)`**

Add to `World.EntityLifecycle.cs`:

```csharp
/// <summary>
/// Destroys all entities matching the given query description.
/// Same semantics as <see cref="DestroyMany"/> — cascade + bulk-remove.
/// </summary>
public void Destroy(in QueryDescription description)
{
    AssertNotDisposed();
    var query = GetAdvancedQuery(in description);
    var archetypes = query.GetArchetypeSpan();

    // Count total entities first (one-pass, no allocation).
    var total = 0;
    for (var i = 0; i < archetypes.Length; i++)
        total += archetypes[i].EntityCount;

    if (total == 0) return;

    // Rent buffer of (Entity, Archetype, Row) for all matched entities.
    var rented = ArrayPool<(Entity E, Archetype A, int Row)>.Shared.Rent(total);
    var written = 0;

    // Bump dead-set generation (same pattern as DestroyMany).
    if (++_destroyCurrentGen == 0)
    {
        Array.Clear(_destroyVisitedGen);
        _destroyCurrentGen = 1;
    }
    EnsureDestroyGenCapacity(_entitySlotCount);

    try
    {
        // Flatten all matched archetypes into the buffer, marking dead
        // as we go. Entities with children are cascade-destroyed inline;
        // childless entities go to the buffer for Phase 2.
        for (var ai = 0; ai < archetypes.Length; ai++)
        {
            var arch = archetypes[ai];
            var entities = arch.GetEntities();

            for (var row = 0; row < entities.Length; row++)
            {
                var entity = entities[row];
                if (IsDead(entity.Id)) continue;

                if (_hierarchy.HasChildren(this, entity))
                {
                    DestroyImpl(entity);
                    for (var d = 0; d < _destroyOrderScratch.Count; d++)
                        MarkDead(_destroyOrderScratch[d].Id);
                }
                else
                {
                    rented[written++] = (entity, arch, row);
                    MarkDead(entity.Id);
                }
            }
        }

        // Phase 2: same grouping + bulk-remove as DestroyMany.
        if (written == 0) return;

        var span = rented.AsSpan(0, written);
        span.Sort(static (a, b) =>
        {
            var archCmp = ReferenceEquals(a.A, b.A) ? 0
                : (nint)a.A < (nint)b.A ? -1 : 1;
            if (archCmp != 0) return archCmp;
            return b.Row.CompareTo(a.Row);
        });

        var groupStart = 0;
        while (groupStart < written)
        {
            var arch = span[groupStart].A;
            var groupEnd = groupStart + 1;
            while (groupEnd < written && ReferenceEquals(span[groupEnd].A, arch))
                groupEnd++;

            var count = groupEnd - groupStart;

            if (count == arch.EntityCount)
            {
                for (var i = groupStart; i < groupEnd; i++)
                    KillEntityRecord(span[i].E);
                arch.ResetCount();
            }
            else
            {
                for (var i = groupStart; i < groupEnd; i++)
                {
                    var row = span[i].Row;
                    arch.RemoveAt(row, out var movedEntity);
                    KillEntityRecord(span[i].E);
                    if (movedEntity.IsValid)
                    {
                        ref var movedRecord = ref _records[movedEntity.Id];
                        movedRecord.Archetype = arch;
                        movedRecord.RowIndex = row;
                    }
                }
            }

            groupStart = groupEnd;
        }
    }
    finally
    {
        ArrayPool<(Entity, Archetype, int)>.Shared.Return(rented);
    }
}
```

**Notes:**
- `GetAdvancedQuery` and `GetArchetypeSpan` already call `EnsureRefreshed` internally — query snapshot is current.
- For `Destroy(query)`, the flatten loop reads all entities via `arch.GetEntities()` which returns `ReadOnlySpan<Entity>`. This is the same flat-cache path used by `GetEntityStorageUnsafe`.
- The Sort helper uses `nint` pointer comparison because `Archetype` is a `sealed class` and we never reorder the array — just group same-pointer entries.

**Step 3: Run tests**

Run: `dotnet test -c Release --filter "Destroy_query"` — all pass.

**Step 4: Run full test suite**

Run: `dotnet test -c Release` — all existing tests still pass.

**Step 5: Commit**

```bash
git add src/MiniArch/Core/World.EntityLifecycle.cs
git commit -m "feat: implement Destroy(QueryDescription)"
```

---

### Task 4: Tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: Add DestroyMany tests**

Add these test methods:

```csharp
[Fact]
public void DestroyMany_empty_entity_list_does_nothing()
{
    var world = new World();
    world.Create<Position>();
    world.DestroyMany([]);
    Assert.Equal(1, world.EntityCount);
}

[Fact]
public void DestroyMany_single_entity_equals_Destroy()
{
    var world = new World();
    var e = world.Create<Position>();
    world.DestroyMany(new[] { e });
    Assert.Equal(0, world.EntityCount);
    Assert.False(world.IsAlive(e));
}

[Fact]
public void DestroyMany_skips_dead_entity()
{
    var world = new World();
    var e = world.Create<Position>();
    world.Destroy(e);
    // Should not throw
    world.DestroyMany(new[] { e });
    Assert.Equal(0, world.EntityCount);
}

[Fact]
public void DestroyMany_cascades_subtree()
{
    var world = new World();
    var parent = world.Create();
    var child = world.Create();
    world.AddChild(parent, child);
    world.DestroyMany(new[] { parent });
    Assert.False(world.IsAlive(parent));
    Assert.False(world.IsAlive(child));
}

[Fact]
public void DestroyMany_dedup_cascaded_children()
{
    var world = new World();
    var parent = world.Create();
    var child = world.Create();
    world.AddChild(parent, child);
    // Both parent and child in input — child should not be double-processed
    world.DestroyMany(new[] { parent, child });
    Assert.False(world.IsAlive(parent));
    Assert.False(world.IsAlive(child));
}

[Fact]
public void DestroyMany_multiple_archetypes()
{
    var world = new World();
    var e1 = world.Create<Position>();
    var e2 = world.Create<Velocity>();
    var e3 = world.Create<Position, Velocity>();
    world.DestroyMany(new[] { e1, e2, e3 });
    Assert.Equal(0, world.EntityCount);
}

[Fact]
public void DestroyMany_full_archetype_clear_uses_ResetCount()
{
    var world = new World();
    var entities = new Entity[100];
    for (var i = 0; i < 100; i++)
        entities[i] = world.Create<Position>();

    world.DestroyMany(entities);
    Assert.Equal(0, world.EntityCount);

    // Verify archetype is still usable after ResetCount
    var e = world.Create<Position>();
    Assert.True(world.IsAlive(e));
}

[Fact]
public void DestroyMany_entity_count_correct()
{
    var world = new World();
    var entities = new Entity[50];
    for (var i = 0; i < 50; i++)
        entities[i] = world.Create<Position>();

    world.DestroyMany(entities.AsSpan(0, 30));
    Assert.Equal(20, world.EntityCount);
}

[Fact]
public void DestroyMany_partial_archetype_clear()
{
    var world = new World();
    var positions = new Entity[10];
    for (var i = 0; i < 10; i++)
        positions[i] = world.Create<Position>();

    world.DestroyMany(positions.AsSpan(0, 4));
    Assert.Equal(6, world.EntityCount);
    // Remaining entities must still be alive
    for (var i = 4; i < 10; i++)
        Assert.True(world.IsAlive(positions[i]));
}

[Fact]
public void DestroyMany_recycles_ids()
{
    var world = new World();
    var e = world.Create<Position>();
    var id = e.Id;
    world.DestroyMany(new[] { e });
    var e2 = world.Create<Position>();
    // Should recycle id
    Assert.Equal(id, e2.Id);
    Assert.True(e2.Version > e.Version);
}
```

**Step 2: Add Destroy(query) tests**

```csharp
[Fact]
public void Destroy_query_all()
{
    var world = new World();
    world.Create<Position>();
    world.Create<Position>();
    world.Create<Velocity>(); // not matched
    world.Destroy(new QueryDescription().With<Position>());
    Assert.Equal(1, world.EntityCount);
}

[Fact]
public void Destroy_query_cascade()
{
    var world = new World();
    var parent = world.Create<Position>();
    var child = world.Create();
    world.AddChild(parent, child);
    world.Destroy(new QueryDescription().With<Position>());
    Assert.False(world.IsAlive(parent));
    Assert.False(world.IsAlive(child));
}

[Fact]
public void Destroy_query_empty_does_nothing()
{
    var world = new World();
    world.Destroy(new QueryDescription().With<Position>());
    Assert.Equal(0, world.EntityCount);
}
```

**Step 3: Run all new tests**

Run: `dotnet test -c Release --filter "DestroyMany|Destroy_query"` — all pass.

**Step 4: Commit**

```bash
git add tests/MiniArch.Tests/Core/WorldLifecycleTests.cs
git commit -m "test: add DestroyMany and Destroy(query) tests"
```

---

### Task 5: Run regression gate

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

Verify: Movement ≥1642, Attack ≥997, no memory growth, no crashes.

```bash
git add .
git commit -m "chore: pass regression gate for destroy-many API"
```
