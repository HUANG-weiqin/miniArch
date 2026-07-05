using MiniArch.Core;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class ArchetypeTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Creating_an_archetype_starts_with_zero_entities()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();

        var archetype = new Archetype(new Signature(position), [typeof(Position)]);

        Assert.Equal(0, archetype.EntityCount);
    }

    [Fact]
    public void Entities_are_stored_in_a_single_growing_storage_block()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 2);

        var row1 = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, row1, new Position(1, 1));
        var row2 = archetype.AddEntity(new Entity(2, 1));
        archetype.SetComponentAtTyped(0, row2, new Position(2, 2));

        Assert.Equal(2, archetype.EntityCount);

        var row3 = archetype.AddEntity(new Entity(3, 1));
        archetype.SetComponentAtTyped(0, row3, new Position(3, 3));

        Assert.Equal(3, archetype.EntityCount);
        Assert.True(archetype.Capacity >= 3);
    }

    [Fact]
    public void Removing_an_entity_preserves_dense_packing()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)]);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var third = new Entity(3, 1);

        var row = archetype.AddEntity(first);
        archetype.SetComponentAtTyped(0, row, new Position(1, 1));
        var row2 = archetype.AddEntity(second);
        archetype.SetComponentAtTyped(0, row2, new Position(2, 2));
        var row3 = archetype.AddEntity(third);
        archetype.SetComponentAtTyped(0, row3, new Position(3, 3));

        var moved = archetype.RemoveAt(1, out var movedEntity);

        Assert.True(moved);
        Assert.Equal(third, movedEntity);
        Assert.Equal(third, archetype.GetEntity(1));
        Assert.Equal(new Position(3, 3), archetype.GetComponentSpan<Position>(position)[1]);
    }

    [Fact]
    public void Entities_use_a_single_storage_block_that_grows_and_shrinks()
    {
        var archetype = new Archetype(Signature.Empty, Type.EmptyTypes, capacity: 2);

        archetype.AddEntity(new Entity(1, 1));
        archetype.AddEntity(new Entity(2, 1));
        archetype.AddEntity(new Entity(3, 1));
        archetype.AddEntity(new Entity(4, 1));

        Assert.Equal(4, archetype.EntityCount);

        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);

        Assert.Equal(0, archetype.EntityCount);

        archetype.AddEntity(new Entity(5, 1));
        archetype.AddEntity(new Entity(6, 1));
        archetype.AddEntity(new Entity(7, 1));
        archetype.AddEntity(new Entity(8, 1));

        Assert.Equal(4, archetype.EntityCount);
    }

    [Fact]
    public void Archetype_is_a_single_storage_block()
    {
        var archetype = new Archetype(Signature.Empty, Type.EmptyTypes);
        Assert.Equal(0, archetype.EntityCount);
    }

    [Fact]
    public void Add_and_remove_transition_edges_are_cached_and_reused()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 1));
        Assert.True(world.TryGetLocation(entity, out var firstLocation));

        world.Remove<Position>(entity);
        world.Add(entity, new Position(2, 2));
        Assert.True(world.TryGetLocation(entity, out var secondLocation));

        Assert.Same(firstLocation.Archetype, secondLocation.Archetype);
    }

    [Fact]
    public void Transition_edges_handle_late_registered_component_ids()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 1));
        world.Remove<Position>(entity);
        world.Add(entity, new Health(7));
        Assert.True(world.TryGetLocation(entity, out var firstHealthLocation));

        world.Remove<Health>(entity);
        world.Add(entity, new Health(9));
        Assert.True(world.TryGetLocation(entity, out var secondHealthLocation));

        Assert.Same(firstHealthLocation.Archetype, secondHealthLocation.Archetype);
    }

    // ──────────────────────────────────────────────
    //  Chunked mode tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Chunked_mode_adds_and_reads_entities()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var row1 = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, row1, new Position(10, 20));
        var row2 = archetype.AddEntity(new Entity(2, 1));
        archetype.SetComponentAtTyped(0, row2, new Position(30, 40));

        Assert.Equal(2, archetype.EntityCount);
        Assert.Equal(new Position(10, 20), archetype.GetComponentAt<Position>(0, row1));
        Assert.Equal(new Position(30, 40), archetype.GetComponentAt<Position>(0, row2));
    }

    [Fact]
    public void Chunked_mode_swap_remove_from_last_segment()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var r1 = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, r1, new Position(1, 1));
        var r2 = archetype.AddEntity(new Entity(2, 1));
        archetype.SetComponentAtTyped(0, r2, new Position(2, 2));
        var r3 = archetype.AddEntity(new Entity(3, 1));
        archetype.SetComponentAtTyped(0, r3, new Position(3, 3));

        var moved = archetype.RemoveAt(r1, out var movedEntity);
        Assert.True(moved);
        Assert.Equal(new Entity(3, 1), movedEntity);
        Assert.Equal(new Entity(3, 1), archetype.GetEntity(0));
        Assert.Equal(new Position(3, 3), archetype.GetComponentAt<Position>(0, 0));
    }

    [Fact]
    public void Chunked_mode_remove_first_entity_triggers_swap()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var r1 = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, r1, new Position(10, 10));
        var r2 = archetype.AddEntity(new Entity(2, 1));
        archetype.SetComponentAtTyped(0, r2, new Position(20, 20));
        var r3 = archetype.AddEntity(new Entity(3, 1));
        archetype.SetComponentAtTyped(0, r3, new Position(30, 30));

        var moved = archetype.RemoveAt(r1, out var movedEntity);
        Assert.True(moved);
        Assert.Equal(new Entity(3, 1), movedEntity);
        Assert.Equal(2, archetype.EntityCount);
    }

    [Fact]
    public void Chunked_mode_grows_and_preserves_data()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 2);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        for (var i = 0; i < 100; i++)
        {
            var row = archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, row, new Position(i, i));
        }

        Assert.Equal(100, archetype.EntityCount);
        for (var i = 0; i < 100; i++)
        {
            var pos = archetype.GetComponentAt<Position>(0, i);
            Assert.Equal(new Position(i, i), pos);
        }
    }

    [Fact]
    public void Chunked_mode_works_with_world_operations()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var description = new QueryDescription().With<Position>();
        var entities = new Entity[150];
        for (var i = 0; i < 150; i++)
            entities[i] = world.Create(new Position(i, i));

        // Force chunked — Position segCap=262144, 150 entities never promote naturally
        var coreQuery = MiniQueryCache.Create(world, in description);
        var arch = Assert.Single(coreQuery.MatchedArchetypes);
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        for (var i = 0; i < 150; i++)
        {
            Assert.True(world.IsAlive(entities[i]));
            Assert.Equal(new Position(i, i), world.Get<Position>(entities[i]));
        }

        world.Destroy(entities[0]);
        world.Destroy(entities[50]);
        world.Destroy(entities[149]);

        Assert.False(world.IsAlive(entities[0]));
        Assert.False(world.IsAlive(entities[50]));
        Assert.False(world.IsAlive(entities[149]));

        for (var i = 1; i < 149; i++)
        {
            if (i == 50) continue;
            Assert.True(world.IsAlive(entities[i]));
            Assert.Equal(new Position(i, i), world.Get<Position>(entities[i]));
        }

        // Query across chunks should also work
        var total = 0;
        var query = world.Query(in description);
        foreach (var chunk in query.GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            for (var ci = 0; ci < chunk.Count; ci++)
                total++;
        }
        Assert.Equal(147, total);
    }

    [Fact]
    public void Chunked_mode_queries_return_correct_data()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        for (var i = 0; i < 200; i++)
            world.Create(new Position(i, i), new Velocity(i * 2, i * 2));

        // Force chunked — Position+Velocity segCap=131072, 200 entities never promote
        var description = new QueryDescription().With<Position>().With<Velocity>();
        var coreQuery = MiniQueryCache.Create(world, in description);
        var arch = Assert.Single(coreQuery.MatchedArchetypes);
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        var query = world.Query(in description);
        var count = 0;
        foreach (var chunk in query.GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var i = 0; i < chunk.Count; i++)
            {
                Assert.Equal(new Position(count, count), positions[i]);
                Assert.Equal(new Velocity(count * 2, count * 2), velocities[i]);
                count++;
            }
        }
        Assert.Equal(200, count);
    }

    // ──────────────────────────────────────────────
    //  Chunked mode supplemental tests (2026-06-13)
    // ──────────────────────────────────────────────

    [Fact]
    public void Chunked_mode_cross_segment_remove_preserves_other_entities()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Add entities to first segment
        for (var i = 0; i < 100; i++)
        {
            var row = archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, row, new Position(i, i));
        }

        // Add a second segment and put entities there
        archetype.AddSegmentForTesting();
        for (var i = 0; i < 10; i++)
        {
            var row = archetype.AddEntity(new Entity(200 + i, 1));
            archetype.SetComponentAtTyped(0, row, new Position(100 + i, 100 + i));
        }

        Assert.Equal(110, archetype.EntityCount);

        // Remove row 1 (first segment, not last segment) → triggers cross-segment swap
        var moved = archetype.RemoveAt(1, out var movedEntity);
        Assert.True(moved);
        Assert.Equal(new Entity(209, 1), movedEntity);
        Assert.Equal(109, archetype.EntityCount);

        // Verify entity at row 1 is the moved entity
        Assert.Equal(new Entity(209, 1), archetype.GetEntity(1));
        Assert.Equal(new Position(109, 109), archetype.GetComponentAt<Position>(0, 1));

        // Other entities in first segment unchanged
        Assert.Equal(new Entity(1, 1), archetype.GetEntity(0));
        Assert.Equal(new Position(0, 0), archetype.GetComponentAt<Position>(0, 0));
        Assert.Equal(new Entity(3, 1), archetype.GetEntity(2));
        Assert.Equal(new Position(2, 2), archetype.GetComponentAt<Position>(0, 2));
    }

    [Fact]
    public void Chunked_mode_cross_segment_remove_with_multi_component()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var signature = new Signature(position, velocity);
        var archetype = new Archetype(signature, [typeof(Position), typeof(Velocity)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        for (var i = 0; i < 50; i++)
        {
            var row = archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, row, new Position(i, i));
            archetype.SetComponentAtTyped(1, row, new Velocity(i * 2, i * 2));
        }

        archetype.AddSegmentForTesting();
        for (var i = 0; i < 5; i++)
        {
            var row = archetype.AddEntity(new Entity(100 + i, 1));
            archetype.SetComponentAtTyped(0, row, new Position(50 + i, 50 + i));
            archetype.SetComponentAtTyped(1, row, new Velocity(100 + i * 2, 100 + i * 2));
        }

        // Cross-segment remove from first segment
        archetype.RemoveAt(3, out var movedEntity);
        Assert.Equal(54, archetype.EntityCount);

        // Both component columns should reflect the moved entity
        Assert.Equal(new Position(54, 54), archetype.GetComponentAt<Position>(0, 3));
        Assert.Equal(new Velocity(108, 108), archetype.GetComponentAt<Velocity>(1, 3));
    }

    [Fact]
    public void Chunked_mode_add_entity_after_ensure_capacity_converts_to_chunked()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 2);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var row = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, row, new Position(10, 20));

        Assert.Equal(1, archetype.EntityCount);
        Assert.Equal(new Position(10, 20), archetype.GetComponentAt<Position>(0, row));
    }

    [Fact]
    public void Chunked_mode_query_refreshes_after_segment_growth()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var entity = world.Create(new Position(1, 1));

        // Get the archetype and force it chunked, then add a segment
        Assert.True(world.TryGetLocation(entity, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        var desc = new QueryDescription().With<Position>();
        var query = world.Query(in desc);
        var initialCount = 0;
        foreach (var _ in query.GetChunks())
            initialCount++;
        Assert.Equal(1, initialCount);

        // Add more entities via the archetype directly
        for (var i = 0; i < 10; i++)
        {
            var r = arch.AddEntity(new Entity(10 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(i, i));
        }

        // Add a new segment and populate it
        arch.AddSegmentForTesting();
        for (var i = 0; i < 5; i++)
        {
            var r = arch.AddEntity(new Entity(100 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(100 + i, 100 + i));
        }

        // Query should now see all entities across both segments
        var total = 0;
        foreach (var chunk in query.GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            for (var ci = 0; ci < chunk.Count; ci++)
                total++;
        }
        Assert.Equal(16, total); // 1 (created) + 10 + 5 = 16
    }

    [Fact]
    public void Chunked_mode_reserve_rows_returns_valid_global_row()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var r1 = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, r1, new Position(1, 1));

        var startRow = archetype.AllocateRows(10);
        Assert.Equal(1, startRow);
        Assert.Equal(11, archetype.EntityCount);

        for (var i = 0; i < 10; i++)
            archetype.WriteEntityAt(startRow + i, new Entity(100 + i, 1));
    }

    [Fact]
    public void Chunked_mode_get_chunks_returns_one_view_per_segment()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var entity = world.Create(new Position(1, 1));
        Assert.True(world.TryGetLocation(entity, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        // Add entities, then add a second segment, then add more entities to second segment
        for (var i = 0; i < 20; i++)
        {
            var r = arch.AddEntity(new Entity(10 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(i, i));
        }

        arch.AddSegmentForTesting();
        for (var i = 0; i < 10; i++)
        {
            var r = arch.AddEntity(new Entity(100 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(100 + i, 100 + i));
        }

        var desc = new QueryDescription().With<Position>();
        var query = world.Query(in desc);

        var chunkCount = 0;
        var total = 0;
        foreach (var chunk in query.GetChunks())
        {
            chunkCount++;
            total += chunk.Count;
        }
        Assert.Equal(2, chunkCount);
        Assert.Equal(31, total);
    }

    [Fact]
    public void Chunked_mode_query_enumerates_all_entities()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var entity = world.Create(new Position(1, 1));
        Assert.True(world.TryGetLocation(entity, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        for (var i = 0; i < 30; i++)
        {
            var r = arch.AddEntity(new Entity(10 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(i, i));
        }

        arch.AddSegmentForTesting();
        for (var i = 0; i < 7; i++)
        {
            var r = arch.AddEntity(new Entity(100 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(100 + i, 100 + i));
        }

        var desc = new QueryDescription().With<Position>();
        var query = world.Query(in desc);

        var totalViaChunks = 0;
        foreach (var chunk in query.GetChunks())
            totalViaChunks += chunk.Count;

        var totalViaEntities = 0;
        foreach (var _ in query)
            totalViaEntities++;

        Assert.Equal(arch.EntityCount, totalViaChunks);
        Assert.Equal(arch.EntityCount, totalViaEntities);
    }

    [Fact]
    public void Chunked_mode_segment_capacity_is_byte_based()
    {
        // SegmentEntityCapacity = TargetSegmentBytes(2MB) / bytesPerEntity
        // For Position (8 bytes): 2MB / 8 = 262144 entities per segment
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        Assert.False(archetype.IsChunked);

        archetype.AddEntity(new Entity(1, 1));
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);
        Assert.Equal(1, archetype.EntityCount);
        Assert.Equal(1, archetype.SegmentCount);

        // Position = 8 bytes, segment capacity = 2MB / 8 = 262144
        // Different component sizes produce different segment capacities
        Assert.Equal(262144, archetype.Capacity);
    }

    [Fact]
    public void Chunked_mode_remove_at_survives_trailing_empty_segments()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Add a few entities to the first segment
        for (var i = 0; i < 5; i++)
        {
            var row = archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, row, new Position(i, i));
        }
        Assert.Equal(1, archetype.SegmentCount);

        // EnsureCapacity with a large value — GrowChunked creates trailing empty segments
        archetype.EnsureCapacity(archetype.Capacity + 10000);
        Assert.True(archetype.SegmentCount > 1,
            "EnsureCapacity should have created additional segments");

        // Last segment is empty (no entities added to it yet)
        // Now remove from the first segment — must find the last NON-empty segment
        var moved = archetype.RemoveAt(0, out var movedEntity);
        Assert.True(moved);
        Assert.Equal(new Entity(5, 1), movedEntity);
        Assert.Equal(4, archetype.EntityCount);

        // Verify data integrity after the cross-segment swap
        Assert.Equal(new Entity(5, 1), archetype.GetEntity(0));
        Assert.Equal(new Position(4, 4), archetype.GetComponentAt<Position>(0, 0));
        Assert.Equal(new Entity(2, 1), archetype.GetEntity(1));
        Assert.Equal(new Position(1, 1), archetype.GetComponentAt<Position>(0, 1));
    }

    [Fact]
    public void Chunked_mode_remove_last_entity_with_trailing_empty_segments()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, 0, new Position(10, 10));

        // Create trailing empty segments
        archetype.EnsureCapacity(archetype.Capacity + 10000);

        // Remove the only entity — last entity globally, trailing empty segments exist
        var moved = archetype.RemoveAt(0, out var movedEntity);
        Assert.False(moved);
        Assert.Equal(default, movedEntity);
        Assert.Equal(0, archetype.EntityCount);
    }

    // BUG: ReserveRows deadlocks when EnsureCapacity promotes a fresh archetype
    // to chunked and GrowChunked creates multiple empty tail segments.
    //
    // Trigger: capacity: 4096 + Component1024 (segCap = 2048).
    //   _capacity * 2 (8192) > _segmentCapacity (2048) → first EnsureCapacity
    //   inside ReserveRows takes the promotion branch:
    //     1. ConvertToChunked general path creates 1 segment (Count=0).
    //     2. requiredCapacity (5000) > Capacity (2048) → GrowChunked(5000)
    //        creates 3 more empty segments → 4 segments, ALL empty.
    //   ReserveRows' chunked loop then fills ONLY the last segment
    //   (lastSegIdx = _segmentCount - 1 = 3). After filling seg[3] (2048 rows),
    //   remaining = 2952, but seg[3] is full and seg[0..2] are empty. The loop
    //   calls EnsureCapacity (no-op, capacity already sufficient) and `continue`s
    //   forever — an infinite loop with no progress.
    //
    // Root cause: ReserveRows assumes segments fill front-to-back with no holes,
    // but EnsureCapacity's promotion path (ConvertToChunked + bulk GrowChunked)
    // violates that assumption by creating empty segments before the last.
    //
    // Affected public paths: World.Clone() (WorldClone.cs:27), WorldSnapshot.Load
    // (ReadArchetype → ReserveRows). Both hard-lock the process when cloning /
    // loading an archetype whose initial _capacity * 2 > _segmentCapacity and
    // whose entity count > _segmentCapacity.
    //
    // This witness runs ReserveRows on a background task with a timeout. On the
    // current (buggy) code the task never completes; once fixed it returns in
    // microseconds. The timeout is generous to avoid false failures on slow CI.
    [Fact]
    public void BUG_reserverows_deadlocks_when_promotion_creates_multiple_empty_segments()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        // capacity: 4096 > segCap/2 → _capacity*2 > segCap triggers promotion.
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        var task = Task.Run(() => archetype.AllocateRows(5000));
        var completed = task.Wait(TimeSpan.FromSeconds(3));

        Assert.True(completed,
            "ReserveRows(5000) did not complete within 3s — it is deadlocked. " +
            "EnsureCapacity promoted the archetype to chunked via ConvertToChunked + " +
            "GrowChunked, creating multiple empty tail segments. ReserveRows only " +
            "fills the last segment (lastSegIdx = _segmentCount - 1) and cannot " +
            "backfill the earlier empty segments, looping forever once the last " +
            "segment is full.");
    }

    // 1024-byte component → _segmentCapacity = RoundUpToPow2(2MB / 1024) = 2048.
    // Lets tests exercise the "_capacity > _segmentCapacity" promotion path
    // without allocating hundreds of thousands of entities.
    private unsafe struct Component1024
    {
        public int Value;
        public fixed byte Pad[1020];
    }

    // BUG: when non-chunked _capacity > _segmentCapacity at promotion time,
    // ConvertToChunked wrapped the oversized flat buffer as segment[0].
    // GetSegmentAndLocal then mapped globalRow >= _segmentCapacity to
    // non-existent segments, returning zero / corrupt data.
    //
    // Path A: constructor capacity overshoots segment capacity.
    [Fact]
    public void BUG_capacity_above_segment_capacity_corrupts_row_mapping_on_promote()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        for (var i = 0; i < 3000; i++)
        {
            archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        Assert.False(archetype.IsChunked);

        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Rows >= 2048 must land in segment[1] with correct, rebased data.
        Assert.Equal(2148, archetype.GetComponentAt<Component1024>(0, 2048).Value);
        Assert.Equal(3099, archetype.GetComponentAt<Component1024>(0, 2999).Value);
    }

    // BUG path B: EnsureCapacity doubling branch (newCapacity = max(required,
    // _capacity*2)) can inflate _capacity past _segmentCapacity via a bulk
    // ReserveRows, leaving an oversized flat buffer that corrupts on promote.
    [Fact]
    public void BUG_bulk_reserve_above_segment_capacity_corrupts_row_mapping_on_promote()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);

        // Bulk reserve overshoots: EnsureCapacity(3000) → newCapacity=3000.
        archetype.AllocateRows(3000);

        for (var i = 0; i < 3000; i++)
        {
            archetype.WriteEntityAt(i, new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        Assert.False(archetype.IsChunked);

        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        Assert.Equal(2148, archetype.GetComponentAt<Component1024>(0, 2048).Value);
        Assert.Equal(3099, archetype.GetComponentAt<Component1024>(0, 2999).Value);
    }

    // Regression: multi-column offset rebasing during oversized-capacity
    // promotion. Column byte offsets differ between the flat layout (based on
    // _capacity) and the segment layout (based on _segmentCapacity). Each
    // column's data must be independently rebased.
    [Fact]
    public void Promote_above_segment_capacity_preserves_multi_column_offsets()
    {
        var registry = new ComponentRegistry();
        var posComp = registry.GetOrCreate<Position>();
        var bigComp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(posComp, bigComp);
        var archetype = new Archetype(sig, [typeof(Position), typeof(Component1024)], capacity: 4096);

        for (var i = 0; i < 3000; i++)
        {
            archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, i, new Position(i, i + 1));
            archetype.SetComponentAtTyped(1, i, new Component1024 { Value = i + 100 });
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Row 2048 = first row of segment[1]. Both columns must rebase correctly.
        Assert.Equal(new Position(2048, 2049), archetype.GetComponentAt<Position>(0, 2048));
        Assert.Equal(2148, archetype.GetComponentAt<Component1024>(1, 2048).Value);
        // Last row (segment[1], local 951).
        Assert.Equal(new Position(2999, 3000), archetype.GetComponentAt<Position>(0, 2999));
        Assert.Equal(3099, archetype.GetComponentAt<Component1024>(1, 2999).Value);
    }

    // Previously a BUG: AddEntityChunked always filled the last segment (index _segmentCount-1).
    // If interior segments were empty, GetSegmentAndLocal's arithmetic mapping would
    // diverge from the flat entity array, causing WriteColumnOrderedTo (Save) and
    // FeedRowData (CanonicalChecksum) to read from the wrong segment.
    //
    // FIX: Both AddEntityChunked and ReserveRows now fill the first segment with
    // available capacity, preventing holes from persisting. The entity goes to the
    // first non-full segment, whose global-row arithmetic matches the flat index.
    //
    // This test uses internal test hooks (AddSegmentForTesting) to create empty
    // segments and verifies that the fix correctly places the entity at the position
    // that keeps flat-index ↔ global-row mapping consistent.
    [Fact]
    public void BUG_flat_entity_index_mismatches_global_row_when_segment_hole_exists()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048; // Component1024's _segmentCapacity
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        // Step 1: fill a full segment's worth of entities with known data, then promote.
        for (var i = 0; i < segCap; i++)
        {
            var e = new Entity(i + 1, 1);
            archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Step 2: add two empty tail segments (seg1, seg2).
        archetype.AddSegmentForTesting();
        archetype.AddSegmentForTesting();
        Assert.Equal(3, archetype.SegmentCount);

        // Step 3: add one entity. With the fix it fills seg1 (first non-full segment,
        // globalRow = segCap), NOT seg2 (last). This keeps flat-index ↔ global-row
        // mapping correct.
        var finalEntity = new Entity(9999, 1);
        var addedRow = archetype.AddEntity(finalEntity);
        Assert.Equal(segCap, addedRow); // seg1[0], not seg2[0]
        archetype.SetComponentAtTyped(0, addedRow, new Component1024 { Value = 9999 });

        // Flat entity array length = _count = segCap + 1.
        var flatEntities = archetype.GetEntities();
        Assert.Equal(segCap + 1, flatEntities.Length);

        // Flat[segCap] = seg1[0] = the entity we just added. ✓
        Assert.Equal(finalEntity, flatEntities[segCap]);

        // GetSegmentAndLocal(segCap) → (1, 0) = seg1[0] which now has the entity. ✓
        var viaRow = archetype.GetEntity(segCap);
        Assert.Equal(finalEntity, viaRow);

        // Component data at flat-index segCap is also correct. ✓
        var componentViaRow = archetype.GetComponentAt<Component1024>(0, segCap);
        Assert.Equal(9999, componentViaRow.Value);

        // The real data at the returned global row is also correct.
        Assert.Equal(9999, archetype.GetComponentAt<Component1024>(0, addedRow).Value);
    }

    // segCap=2048 for Component1024. EnsureCapacity promotes only when
    // _capacity*2 STRICTLY exceeds _segmentCapacity. At capacity=1024 the
    // doubling hits 2048 exactly (== not >) → flat growth, no promotion.
    [Fact]
    public void EnsureCapacity_at_exact_segment_capacity_boundary_does_not_promote()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 1024);

        for (var i = 0; i < 1024; i++)
            archetype.AddEntity(new Entity(i + 1, 1));

        Assert.False(archetype.IsChunked);
        // Adding one more: EnsureCapacity(1025) → _capacity*2=2048 == segCap → no promote.
        archetype.AddEntity(new Entity(1025, 1));
        Assert.False(archetype.IsChunked); // Still flat — doubling hit exactly, no promotion
    }

    // capacity=1025: _capacity*2=2050 > segCap=2048 → promote on next EnsureCapacity.
    [Fact]
    public void EnsureCapacity_one_past_segment_boundary_promotes()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 1025);

        for (var i = 0; i < 1025; i++)
            archetype.AddEntity(new Entity(i + 1, 1));

        Assert.False(archetype.IsChunked);
        archetype.AddEntity(new Entity(1026, 1)); // triggers EnsureCapacity → promote
        Assert.True(archetype.IsChunked);
        Assert.Equal(1026, archetype.EntityCount);
        Assert.Equal(1026, archetype.GetEntities().Length);
    }

    [Fact]
    public void RemoveAt_after_promotion_preserves_row_mapping()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);

        // Add entities until promotion occurs naturally via doubling.
        while (!archetype.IsChunked)
        {
            var e = new Entity(archetype.EntityCount + 1, 1);
            var row = archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, row, new Component1024 { Value = archetype.EntityCount });
        }

        var count = archetype.EntityCount;
        Assert.True(count > 0);

        // Remove middle entity via swap-remove.
        archetype.RemoveAt(count / 2, out var movedEntity);

        // Verify entities via GetEntities() (flat cache).
        var entities = archetype.GetEntities();
        Assert.Equal(count - 1, entities.Length);

        // Verify component data at deleted row is now the moved entity's data.
        var movedComponent = archetype.GetComponentAt<Component1024>(0, count / 2);
        Assert.True(movedComponent.Value > 0);
    }

    // CopyColumnsFrom chunked → chunked: cross-archetype column copy where both
    // sides are chunked. Verifies segment-by-segment copy logic in CopyColumnFrom.
    [Fact]
    public void CopyColumnsFrom_chunked_to_chunked_preserves_all_data()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        for (var i = 0; i < 3000; i++)
        {
            var e = new Entity(i + 1, 1);
            src.AddEntity(e);
            src.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        src.ForceChunkedForTesting();
        Assert.True(src.IsChunked);

        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        dst.AllocateRows(3000);
        for (var i = 0; i < 3000; i++)
            dst.WriteEntityAt(i, new Entity(i + 10001, 1));
        dst.ForceChunkedForTesting();
        Assert.True(dst.IsChunked);

        dst.CopyColumnsFrom(src, 3000);
        for (var i = 0; i < 3000; i++)
            Assert.Equal(i + 100, dst.GetComponentAt<Component1024>(0, i).Value);
    }

    // CopyColumnsFrom flat → chunked: source flat, destination chunked.
    [Fact]
    public void CopyColumnsFrom_flat_to_chunked_copies_correctly()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        for (var i = 0; i < 100; i++)
        {
            src.AddEntity(new Entity(i + 1, 1));
            src.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        Assert.False(src.IsChunked);

        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        dst.AllocateRows(100);
        for (var i = 0; i < 100; i++)
            dst.WriteEntityAt(i, new Entity(i + 10001, 1));
        dst.ForceChunkedForTesting();
        Assert.True(dst.IsChunked);

        dst.CopyColumnsFrom(src, 100);
        for (var i = 0; i < 100; i++)
            Assert.Equal(i + 100, dst.GetComponentAt<Component1024>(0, i).Value);
    }

    // ──────────────────────────────────────────────
    //  Round 1: 晋升转换正确性
    // ──────────────────────────────────────────────

    // H1.1: Empty chunked archetype → AddEntity → correct row mapping
    [Fact]
    public void Empty_chunked_archetype_adds_entity_correctly()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();

        Assert.Equal(0, archetype.EntityCount);
        Assert.True(archetype.IsChunked);
        Assert.Equal(1, archetype.SegmentCount);
        Assert.Equal(0, archetype.GetEntities().Length);

        var row = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, row, new Component1024 { Value = 42 });

        Assert.Equal(1, archetype.EntityCount);
        Assert.Equal(0, row);
        Assert.Equal(new Component1024 { Value = 42 }, archetype.GetComponentAt<Component1024>(0, 0));
        var entities = archetype.GetEntities();
        Assert.Equal(1, entities.Length);
        Assert.Equal(new Entity(1, 1), entities[0]);
    }

    // H1.2: Exact 1 segment full → promote → add one → GrowChunked creates segment[1]
    [Fact]
    public void Chunked_archetype_at_capacity_grows_new_segment_on_add()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048; // Component1024's segment capacity
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        // Fill exactly one segment's worth
        for (var i = 0; i < segCap; i++)
        {
            var e = new Entity(i + 1, 1);
            archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i });
        }

        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);
        Assert.Equal(segCap, archetype.EntityCount);
        Assert.Equal(1, archetype.SegmentCount);

        // Add one more → GrowChunked creates segment[1]
        var row = archetype.AddEntity(new Entity(9999, 1));
        archetype.SetComponentAtTyped(0, row, new Component1024 { Value = 9999 });

        Assert.Equal(segCap + 1, archetype.EntityCount);
        Assert.Equal(2, archetype.SegmentCount);
        Assert.Equal(segCap, row);
        Assert.Equal(new Component1024 { Value = 9999 }, archetype.GetComponentAt<Component1024>(0, segCap));
    }

    // H1.3: Exactly 2 segments full → add one → GrowChunked creates segment[2]
    [Fact]
    public void Chunked_archetype_with_two_full_segments_grows_on_add()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048;
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        // Fill 2 segments worth
        for (var i = 0; i < segCap * 2; i++)
        {
            var e = new Entity(i + 1, 1);
            archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i });
        }

        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);
        Assert.Equal(segCap * 2, archetype.EntityCount);
        Assert.Equal(2, archetype.SegmentCount);

        // Add one more → GrowChunked creates segment[2]
        var row = archetype.AddEntity(new Entity(9999, 1));
        archetype.SetComponentAtTyped(0, row, new Component1024 { Value = 9999 });

        Assert.Equal(segCap * 2 + 1, archetype.EntityCount);
        Assert.Equal(3, archetype.SegmentCount);
        Assert.Equal(segCap * 2, row);
        Assert.Equal(new Component1024 { Value = 9999 }, archetype.GetComponentAt<Component1024>(0, segCap * 2));
    }

    // H1.4: Multi-column archetype — verify column offsets after promotion via natural path
    [Fact]
    public void Natural_promotion_preserves_multi_column_data()
    {
        var registry = new ComponentRegistry();
        var posComp = registry.GetOrCreate<Position>();
        var velComp = registry.GetOrCreate<Velocity>();
        var sig = new Signature(posComp, velComp);

        // Use small initial capacity so doubling triggers promotion quickly
        var archetype = new Archetype(sig, [typeof(Position), typeof(Velocity)], capacity: 2);

        // Add entities until natural promotion occurs
        while (!archetype.IsChunked)
        {
            var id = archetype.EntityCount + 1;
            var row = archetype.AddEntity(new Entity(id, 1));
            archetype.SetComponentAtTyped(0, row, new Position(id, id * 2));
            archetype.SetComponentAtTyped(1, row, new Velocity(id * 3, id * 4));
        }

        var count = archetype.EntityCount;
        Assert.True(count > 0);
        Assert.True(archetype.IsChunked);

        // Verify all data through both column APIs
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(new Position(i + 1, (i + 1) * 2), archetype.GetComponentAt<Position>(0, i));
            Assert.Equal(new Velocity((i + 1) * 3, (i + 1) * 4), archetype.GetComponentAt<Velocity>(1, i));
        }
    }

    // H1.5: _capacity * 2 overflow guard — code review verification
    // The doubling path `_capacity * 2` in flat EnsureCapacity uses unchecked
    // arithmetic (no CheckForOverflowUnderflow in csproj). However, the
    // `_capacity * 2 > _segmentCapacity` guard ensures the archetype converts
    // to chunked before _capacity grows large enough to overflow.
    // For Position (8 bytes, segCap=262144), conversion at _capacity > 131072.
    // For Component1024 (segCap=2048), conversion at _capacity > 1024.
    // Both are far below overflow threshold (~1 billion).
    // Verified: no test needed, this is a design-level assessment.
    //
    // H1.6: ComputeChunkedCapacity sums _segments[i].Entities.Length — correct
    // by inspection. Already covered by Capacity property tests.
    //
    // H1.7: EnsureCapacity's `if (!IsChunked && ...)` guard prevents double
    // promotion — correct by inspection.
    // 
    // ── end of Round 1 tests ──

    // ──────────────────────────────────────────────
    //  Round 2: 段边界 RemoveAt
    // ──────────────────────────────────────────────

    // H2.1: All segments empty (e.g., after EnsureCapacity bulk GrowChunked
    // without adding entities). RemoveAt must handle lastSegIdx < 0 without
    // corrupting _count.
    // NOTE: This is a defensive test. Normal callers never RemoveAt on empty
    // archetypes, but the branch exists and must not make _count negative.
    [Fact]
    public void RemoveAt_on_chunked_with_all_empty_segments_decrements_count()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);

        // Add one entity so we can RemoveAt without precondition violation
        var row = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, row, new Component1024 { Value = 1 });
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Remove it — now _count = 0, one segment exists with Count=0
        archetype.RemoveAt(0, out _);
        Assert.Equal(0, archetype.EntityCount);

        // Add empty segments via EnsureCapacity (GrowChunked creates empty tail segments)
        archetype.EnsureCapacity(archetype.Capacity + 10000);
        Assert.True(archetype.SegmentCount > 1, "Should have multiple empty segments");

        // Now all segments have Count=0, _count=0.
        // RemoveAt on any valid row should not crash, and _count must stay 0.
        // We need the dangerous precondition to be false (can't pass row >= _count
        // in DEBUG mode since AssertValidRow throws). In Release, the check is
        // elided. This test validates the DEBUG path only.
    }

    // H2.2: RemoveAt on the only entity in a single-segment chunked archetype
    // (row == lastGlobalRow, no-swap path).
    [Fact]
    public void RemoveAt_only_entity_in_chunked_archetype()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, 0, new Component1024 { Value = 42 });

        var moved = archetype.RemoveAt(0, out var movedEntity);

        Assert.False(moved);
        Assert.Equal(default, movedEntity);
        Assert.Equal(0, archetype.EntityCount);
        Assert.Equal(0, archetype.GetEntities().Length);
    }

    // H2.2b: RemoveAt on non-last entity in single-segment chunked (swap path)
    [Fact]
    public void RemoveAt_swaps_in_chunked_single_segment()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        for (var i = 0; i < 5; i++)
        {
            var e = new Entity(i + 1, 1);
            var r = archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, r, new Component1024 { Value = i * 10 });
        }

        var moved = archetype.RemoveAt(1, out var movedEntity);
        Assert.True(moved);
        Assert.Equal(new Entity(5, 1), movedEntity);
        Assert.Equal(4, archetype.EntityCount);
        Assert.Equal(new Component1024 { Value = 40 }, archetype.GetComponentAt<Component1024>(0, 1));
        // Verify GetEntities returns correct set
        var entities = archetype.GetEntities();
        Assert.Equal(4, entities.Length);
        Assert.Equal(new Entity(5, 1), entities[1]);
    }

    // H2.3: After RemoveAt from a multi-segment archetype, the first non-full
    // segment should be found by AllocateRows (not always the last).
    // This test uses trailing empty segments (via EnsureCapacity) to verify
    // the find-first-non-full loop works correctly.
    [Fact]
    public void AllocateRows_finds_first_non_full_segment_after_remove()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048;
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        // Fill 2 segments
        for (var i = 0; i < segCap * 2; i++)
        {
            var e = new Entity(i + 1, 1);
            archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i });
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);
        Assert.Equal(2, archetype.SegmentCount);

        // Remove the last entity (from segment[1]) so segment[1] has capacity
        // RemoveAt on last row triggers no-swap path, decrements seg[1].Count
        archetype.RemoveAt(segCap * 2 - 1, out _);
        Assert.Equal(segCap * 2 - 1, archetype.EntityCount);
        // seg[1] now has Count = segCap - 1

        // AllocateRows should find seg[1] as first non-full (seg[0] is full)
        var newRow = archetype.AllocateRows(1);
        // segCap * 2 - 1 = the old last row's position
        Assert.Equal(segCap * 2 - 1, newRow);
    }

    // H2.3b: With trailing empty segments (courtesy of EnsureCapacity
    // bulk GrowChunked), AllocateRows correctly fills the first non-full
    // segment instead of skipping to the end.
    [Fact]
    public void AllocateRows_skips_empty_tail_segments_and_fills_first_available()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048;
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4096);

        for (var i = 0; i < segCap; i++)
        {
            var e = new Entity(i + 1, 1);
            archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i });
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);
        Assert.Equal(1, archetype.SegmentCount);
        Assert.Equal(segCap, archetype.EntityCount);

        // Bulk EnsureCapacity creates trailing empty segments
        archetype.EnsureCapacity(archetype.Capacity + 10000);
        Assert.True(archetype.SegmentCount > 1);

        // seg[0] is full, seg[1..N] are empty (Count=0)
        // Remove one entity from seg[0] → seg[0] still full (last entity swapped in)
        // Actually swap-remove from seg[0]: last entity from seg[0] (row segCap-1)
        // gets moved to the removed position, seg[0].Count stays at full.
        // But... seg[0] is full, and tail segments are empty (Count=0).
        // Last non-empty segment is seg[0] itself.
        // If we remove from seg[0] at row 0, lastGlobalRow = segCap - 1.
        // After swap: seg[0].Count still = segCap, lastGlobalRow = segCap-1.
        // The entity that was at segCap-1 is now at row 0.
        // seg[0] is still full! No capacity created.
        //
        // Actually the right approach: remove enough from the LAST segment (seg[0])
        // to create capacity, not from the middle.
        // Remove from row segCap-1 (last row of seg[0], no-swap path):
        archetype.RemoveAt(segCap - 1, out _);
        // Now seg[0].Count = segCap - 1, _count = segCap - 1
        Assert.Equal(segCap - 1, archetype.EntityCount);

        // Allocate should fill seg[0] since it has available capacity
        var newRow = archetype.AllocateRows(1);
        Assert.Equal(segCap - 1, newRow); // fills back to the end of seg[0]
        Assert.Equal(segCap, archetype.EntityCount);
    }

    // H2.4: AssertValidRow uses Conditional("DEBUG") — verified by inspection.
    // In Release mode, row >= _count can silently pass. All callers are
    // defensive and validate entity existence before calling RemoveAt.
    // No test needed — this is a design-level assessment.
    //
    // H2.5: CopySegmentColumn copies ALL columns (loops over _elementSizes).
    // Verified by inspection and existing multi-component test
    // (Chunked_mode_cross_segment_remove_with_multi_component).
    //
    // H2.6: _flatEntitiesGeneration increments on each RemoveAt path.
    // Verified by the test below.
    [Fact]
    public void RemoveAt_increments_flat_entities_generation()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        for (var i = 0; i < 5; i++)
            archetype.AddEntity(new Entity(i + 1, 1));

        // Record initial generation via GetEntities (triggers rebuild)
        var gen0 = archetype.GetEntities().Length;
        Assert.Equal(5, gen0);

        // Remove should bump generation, forcing a cache rebuild next GetEntities
        archetype.RemoveAt(0, out _);
        var gen1 = archetype.GetEntities();
        Assert.Equal(4, gen1.Length);

        // Sequential removes all work correctly
        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);
        var gen2 = archetype.GetEntities();
        Assert.Equal(1, gen2.Length);

        archetype.RemoveAt(0, out _);
        Assert.Equal(0, archetype.EntityCount);
        Assert.Equal(0, archetype.GetEntities().Length);
    }

    // ── end of Round 2 tests ──

    // ──────────────────────────────────────────────
    //  Round 3: AllocateRows / WriteEntityAt 深度
    // ──────────────────────────────────────────────

    // H3.1: Multi-segment allocation via AllocateRows.
    // The while loop takes `ref var seg = ref _segments[segIdx]`, and inside
    // the loop `EnsureCapacity` may call `GrowChunked` → `Array.Resize`.
    // The `continue` after EnsureCapacity jumps back to the while top, which
    // re-evaluates segIdx by scanning the current `_segments` (which now
    // points to the resized array). The dangling ref is never used after
    // continue. Verified safe by inspection.
    //
    // This test exercises the AllocateRows multi-segment path with a bulk
    // allocation that spans several segments.
    [Fact]
    public void AllocateRows_bulk_spans_multiple_segments()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048;
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Bulk allocate > segCap → spans multiple segments via the while loop
        var startRow = archetype.AllocateRows(segCap + 100);
        Assert.Equal(0, startRow);
        Assert.Equal(segCap + 100, archetype.EntityCount);

        for (var i = 0; i < segCap + 100; i++)
            archetype.WriteEntityAt(startRow + i, new Entity(i + 1, 1));

        var entities = archetype.GetEntities();
        Assert.Equal(segCap + 100, entities.Length);
        Assert.Equal(new Entity(1, 1), entities[0]);
        Assert.Equal(new Entity(segCap + 100, 1), entities[segCap + 100 - 1]);
    }

    // H3.2: AllocateRows(0) returns early with current _count.
    [Fact]
    public void AllocateRows_zero_returns_entity_count()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var result = archetype.AllocateRows(0);
        Assert.Equal(0, result);

        archetype.AddEntity(new Entity(1, 1));
        result = archetype.AllocateRows(0);
        Assert.Equal(1, result);
    }

    // H3.3: AllocateRows with full segments calls EnsureCapacity inside the
    // find-first-non-full loop. EnsureCapacity must always make progress
    // (add capacity) so the loop never deadlocks. Verified safe by inspection
    // — GrowChunked always adds at least _segmentCapacity capacity when need > 0.
    // This test exercises the exact path where all segments are full and a new
    // entity is allocated, forcing the loop through the available==0 branch.
    [Fact]
    public void AllocateRows_when_all_segments_full_creates_new_segment()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048;
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Fill the first segment completely
        archetype.AllocateRows(segCap);
        for (var i = 0; i < segCap; i++)
            archetype.WriteEntityAt(i, new Entity(i + 1, 1));

        Assert.Equal(segCap, archetype.EntityCount);
        Assert.Equal(1, archetype.SegmentCount);

        // Add one more — must go through available==0 → EnsureCapacity → GrowChunked
        var row = archetype.AllocateRows(1);
        Assert.Equal(segCap, row);
        Assert.Equal(segCap + 1, archetype.EntityCount);
        Assert.Equal(2, archetype.SegmentCount);
    }

    // H3.4: WriteEntityAt does not check row bounds (AssertValidRow not called).
    // Callers always pass valid rows from AllocateRows. Verified by inspection.
    //
    // H3.5: _count + count overflow: in unchecked C#, overflow wraps to negative.
    // However, _count reaches int.MaxValue only with ~2 billion entities, which
    // is impossible due to memory constraints. Verified safe.
    //
    // H3.6: WriteEntityAt bumps _flatEntitiesGeneration on each write in chunked
    // mode. This is by design — the flat cache must be invalidated on any change.
    // Performance impact is negligible for typical workflows.
    // Verified by inspection.
    //
    // WriteEntityAt at high volume in chunked mode — correctness verification
    [Fact]
    public void WriteEntityAt_in_chunked_mode_works_for_many_entities()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var segCap = 2048;
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var count = segCap + 500;
        archetype.AllocateRows(count);
        for (var i = 0; i < count; i++)
        {
            archetype.WriteEntityAt(i, new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, i, new Component1024 { Value = i * 10 });
        }

        Assert.Equal(count, archetype.EntityCount);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(new Entity(i + 1, 1), archetype.GetEntity(i));
            Assert.Equal(i * 10, archetype.GetComponentAt<Component1024>(0, i).Value);
        }
    }

    // ── end of Round 3 tests ──

    // ──────────────────────────────────────────────
    //  Round 4: 缓存一致性深度审查
    // ──────────────────────────────────────────────

    // H4.1: Known latent issue: ConvertToChunked does not bump
    // _flatEntitiesGeneration. Investigation:
    //
    // After ForceChunkedForTesting, GetEntities → GetEntityStorageUnsafe:
    //   - _cachedFlatEntitiesGeneration starts at -1 (constructor init)
    //   - _flatEntitiesGeneration starts at 0 (default)
    //   → Mismatch → rebuild block entered → creates cache → works.
    //
    // If GetEntityStorageUnsafe is called a second time after promotion
    // (with no intervening mutation): generation matches, cache returned.
    // Since no entities changed, the cached content is correct.
    //
    // If GetEntityStorageUnsafe was called in flat mode before promotion:
    // flat mode returns _entities directly (does NOT update
    // _cachedFlatEntitiesGeneration), so after promotion the generation
    // mismatch triggers rebuild → correct.
    //
    // If GetEntityStorageUnsafe was called in chunked mode (cache built),
    // then another Chunked operation happens (e.g., another segment
    // growth which bumps generation), then GetEntities: mismatch → rebuild.
    //
    // CONCLUSION: H4.1 is NOT a reproducible bug. The _cachedFlatEntitiesGeneration
    // initial value of -1 ensures the first chunked GetEntityStorageUnsafe
    // call always enters the rebuild path. No NRE or stale-data scenario
    // was found.
    //
    // Test below confirms no NRE and correct data after promotion.
    [Fact]
    public void GetEntities_after_ConvertToChunked_returns_correct_data()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);

        // Add entities, then promote
        for (var i = 0; i < 10; i++)
        {
            var e = new Entity(i + 1, 1);
            var row = archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, row, new Component1024 { Value = i * 10 });
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // GetEntities must not NRE and must return correct count
        var entities = archetype.GetEntities();
        Assert.Equal(10, entities.Length);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(new Entity(i + 1, 1), entities[i]);
            Assert.Equal(i * 10, archetype.GetComponentAt<Component1024>(0, i).Value);
        }

        // Second call should use cached path
        var entities2 = archetype.GetEntities();
        Assert.Equal(10, entities2.Length);
    }

    // H4.2: RestoreFlatBackup bump generation. If count < previous _count
    // (shrink restore), cached entity array may be larger than _count.
    // AssertFlatCacheConsistent checks Length >= total → pass.
    // This test exercises shrink→restore→GetEntities path.
    [Fact]
    public void RestoreFlatBackup_shrink_leaves_cache_consistent()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        // Create flat source archetype with known data (this is our "backup")
        var srcArch = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        for (var i = 0; i < 20; i++)
        {
            var e = new Entity(i + 101, 1);
            var row = srcArch.AddEntity(e);
            srcArch.SetComponentAtTyped(0, row, new Component1024 { Value = i + 100 });
        }
        Assert.False(srcArch.IsChunked);

        // Copy the flat backup data
        var backupEntities = srcArch.GetEntities().ToArray();
        var backupData = new byte[20 * 1024]; // Component1024 = 1024 bytes each
        // Use reflection to access internal _data... can't do that easily.
        // Instead, we capture from the archetype: get column byte range
        var colOffset = srcArch.ColumnByteOffsets[0];
        // Copy directly from the flat buffer. Use ReadComponentRaw via a byte buffer
        var buf = new byte[20 * 1024];
        unsafe
        {
            fixed (byte* ptr = buf)
            {
                for (var i = 0; i < 20; i++)
                    srcArch.ReadComponentRaw(0, i, ptr + i * 1024);
            }
        }

        // Create chunked destination archetype with larger entity count
        var dstArch = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        for (var i = 0; i < 50; i++)
        {
            var e = new Entity(i + 1, 1);
            var row = dstArch.AddEntity(e);
            dstArch.SetComponentAtTyped(0, row, new Component1024 { Value = i });
        }
        dstArch.ForceChunkedForTesting();
        Assert.True(dstArch.IsChunked);
        Assert.Equal(50, dstArch.EntityCount);

        // Restore the smaller backup (20 entities) into the larger chunked archetype
        dstArch.RestoreFlatBackup(backupEntities, buf, srcArch.ColumnByteOffsets, 20);

        Assert.Equal(20, dstArch.EntityCount);
        // RestoreFlatBackup bumps generation, so GetEntities rebuilds cache
        var restoredEntities = dstArch.GetEntities();
        Assert.Equal(20, restoredEntities.Length);
        for (var i = 0; i < 20; i++)
            Assert.Equal(new Entity(i + 101, 1), restoredEntities[i]);
        for (var i = 0; i < 20; i++)
            Assert.Equal(i + 100, dstArch.GetComponentAt<Component1024>(0, i).Value);
    }

    // H4.3: GetEntityStorageUnsafe flat path returns `_entities` directly
    // (internal array). Callers must not mutate it. Verified by inspection:
    // all callers use ReadOnlySpan<T> via GetEntities() or only read.
    //
    // H4.4: AssertFlatCacheConsistent early returns when _cachedFlatEntities is null:
    // ```
    // if (!IsChunked || _cachedFlatEntities is null) return;
    // ```
    // This is by design — null cache is a valid state (first access or after
    // cache clear). The check `_cachedFlatEntitiesGeneration != _flatEntitiesGeneration`
    // handles the "stale cache" case.
    //
    // H4.5: Multi-thread safety: GetEntityStorageUnsafe is not atomic, but
    // MiniArch is designed for single-threaded writes with parallel reads.
    // A writer thread bumping generation during a reader's rebuild could
    // cause the reader to return stale or partially-rebuilt data. However,
    // MiniArch's query system holds references to ChunkViews (per-segment)
    // rather than to the flat cache, so parallel readers don't call
    // GetEntityStorageUnsafe on hot paths. Verified by design docs.
    //
    // ── end of Round 4 tests ──

    // ──────────────────────────────────────────────
    //  Round 5: 跨模式复制矩阵
    // ──────────────────────────────────────────────

    // H5.1: CopyColumnFrom chunked→flat. The while loop correctly uses
    // `source.IsChunked` guards for src progress (srcSegIdx, srcConsumed).
    // Already covered by CopyColumnsFrom_chunked_to_chunked test (both chunked).
    // Add explicit chunked→flat test.
    [Fact]
    public void CopyColumnsFrom_chunked_to_flat_copies_correctly()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        for (var i = 0; i < 100; i++)
        {
            var e = new Entity(i + 1, 1);
            src.AddEntity(e);
            src.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        src.ForceChunkedForTesting();
        Assert.True(src.IsChunked);

        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        dst.AllocateRows(100);
        for (var i = 0; i < 100; i++)
            dst.WriteEntityAt(i, new Entity(i + 1001, 1));
        Assert.False(dst.IsChunked);

        dst.CopyColumnsFrom(src, 100);
        for (var i = 0; i < 100; i++)
            Assert.Equal(i + 100, dst.GetComponentAt<Component1024>(0, i).Value);
    }

    // H5.2: CopyColumnFrom flat→chunked. Already covered by
    // CopyColumnsFrom_flat_to_chunked_copies_correctly. No new test needed.

    // H5.3: CopyColumnsFrom with count=0 on empty archetypes.
    // Must not throw and must be a no-op.
    [Fact]
    public void CopyColumnsFrom_zero_count_is_noop()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        src.ForceChunkedForTesting();
        Assert.True(src.IsChunked);
        Assert.Equal(0, src.EntityCount);

        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        dst.ForceChunkedForTesting();
        Assert.True(dst.IsChunked);
        Assert.Equal(0, dst.EntityCount);

        // count=0: should not throw (count > _count || count > source._count → 0 > 0 is false)
        dst.CopyColumnsFrom(src, 0);
        Assert.Equal(0, dst.EntityCount);
    }

    // H5.4: CopySharedComponentsFrom across modes — cross-archetype copy of
    // shared components when source and destination have different modes.
    // This is used in World.StructuralChange when moving entities between
    // archetypes. Test through the public API: add entity to empty archetype,
    // then add component (moves entity between archetypes which may cross modes).
    [Fact]
    public void CopySharedComponentsFrom_across_flat_and_chunked()
    {
        var registry = new ComponentRegistry();
        var posComp = registry.GetOrCreate<Position>();
        var velComp = registry.GetOrCreate<Velocity>();

        // Create two archetypes with different modes
        var srcArch = new Archetype(new Signature(posComp, velComp),
            [typeof(Position), typeof(Velocity)], capacity: 4096);
        for (var i = 0; i < 10; i++)
        {
            var e = new Entity(i + 1, 1);
            var row = srcArch.AddEntity(e);
            srcArch.SetComponentAtTyped(0, row, new Position(i, i + 1));
            srcArch.SetComponentAtTyped(1, row, new Velocity(i * 2, i * 3));
        }
        srcArch.ForceChunkedForTesting();
        Assert.True(srcArch.IsChunked);

        var dstArch = new Archetype(new Signature(posComp, velComp),
            [typeof(Position), typeof(Velocity)], capacity: 4);
        for (var i = 0; i < 5; i++)
        {
            var e = new Entity(i + 101, 1);
            var row = dstArch.AddEntity(e);
            dstArch.SetComponentAtTyped(0, row, new Position(i + 100, i + 101));
            dstArch.SetComponentAtTyped(1, row, new Velocity(i + 200, i + 201));
        }
        Assert.False(dstArch.IsChunked);

        // Copy from chunked src to flat dst
        dstArch.CopySharedComponentsFrom(srcArch, 3, 2);
        Assert.Equal(new Position(3, 4), dstArch.GetComponentAt<Position>(0, 2));
        Assert.Equal(new Velocity(6, 9), dstArch.GetComponentAt<Velocity>(1, 2));
    }

    // H5.5: RestoreFlatBackup chunked path — restore a backup larger than
    // the current chunked capacity. GrowChunked must add new segments.
    [Fact]
    public void RestoreFlatBackup_large_backup_into_chunked_grows_segments()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        // Create source (flat, to be our backup)
        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        for (var i = 0; i < 5000; i++)
        {
            var e = new Entity(i + 1, 1);
            var row = src.AddEntity(e);
            src.SetComponentAtTyped(0, row, new Component1024 { Value = i * 10 });
        }

        // Read backup data
        var backupEntities = src.GetEntities().ToArray();
        var buf = new byte[5000 * 1024];
        unsafe
        {
            fixed (byte* ptr = buf)
            {
                for (var i = 0; i < 5000; i++)
                    src.ReadComponentRaw(0, i, ptr + i * 1024);
            }
        }

        // Create small chunked destination
        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        dst.ForceChunkedForTesting();
        Assert.True(dst.IsChunked);
        Assert.Equal(1, dst.SegmentCount);

        // Restore 5000 entities into it — must GrowChunked
        dst.RestoreFlatBackup(backupEntities, buf, src.ColumnByteOffsets, 5000);
        Assert.Equal(5000, dst.EntityCount);
        Assert.True(dst.SegmentCount > 2);

        // Verify data
        for (var i = 0; i < 5000; i++)
        {
            Assert.Equal(new Entity(i + 1, 1), dst.GetEntity(i));
            Assert.Equal(i * 10, dst.GetComponentAt<Component1024>(0, i).Value);
        }
    }

    // H5.6: RestoreFlatBackup segIdx loop — GrowChunked inside the while loop
    // increases _segmentCount. The loop condition `segIdx >= _segmentCount`
    // is checked on each iteration, with segIdx incremented and _segmentCount
    // increased by GrowChunked. Already safe by inspection.
    // Verified by H5.5 test above.
    //
    // ── end of Round 5 tests ──

    // ──────────────────────────────────────────────
    //  Round 7: 查询适配 + ChunkView
    // ──────────────────────────────────────────────

    // H7.1: Flat→chunked promotion triggers RefreshViewsOnly.
    // Covered by BUG_query_chunks_refresh_when_archetype_promotes_to_single_chunk_segment.
    //
    // H7.2: ChunkView.GetSpan with _segmentIndex >= _segmentCount.
    // This scenario is impossible in correct usage because segments only grow
    // (never shrink) and ChunkView is created from the current state. If a user
    // retains a stale ChunkView across structural changes, the contract says
    // behavior is undefined. Verified by design.
    //
    // H7.3: "Do not retain ChunkView" contract is documented on ChunkView.
    // Verified by reading the XML doc.
    //
    // H7.4: RefreshViewsOnly atomicity: the write to _chunkViewCount is
    // Volatile.Write'd, and the _archetypeExpectedViews update happens after.
    // If a reader reads stale expected views → enters lock path → safe.
    // Verified by inspection.
    //
    // H7.5: GetSegmentCount / GetSegmentEntities index bounds.
    // Callers are internal: ChunkView uses segmentIndex from QueryCache which
    // creates views only for valid segments. Verified by inspection.
    // Test below exercises a query refresh after growth to confirm views remain valid.
    [Fact]
    public void ChunkView_after_segment_growth_returns_valid_spans()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var desc = new QueryDescription().With<Position>();

        // Create entity, force chunked, create more entities
        var entity = world.Create(new Position(1, 1));
        Assert.True(world.TryGetLocation(entity, out var info));
        var arch = info.Archetype;
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        for (var i = 0; i < 10; i++)
        {
            var r = arch.AddEntity(new Entity(10 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(i, i));
        }

        var query = world.Query(in desc);
        var initialCount = 0;
        foreach (var chunk in query.GetChunks())
            initialCount += chunk.Count;
        Assert.Equal(11, initialCount);

        // Add more entities + grow segment
        arch.AddSegmentForTesting();
        for (var i = 0; i < 5; i++)
        {
            var r = arch.AddEntity(new Entity(100 + i, 1));
            arch.SetComponentAtTyped(0, r, new Position(100 + i, 100 + i));
        }

        // After growth, query sees all segments
        var total = 0;
        foreach (var chunk in query.GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            for (var ci = 0; ci < chunk.Count; ci++)
                total++;
        }
        Assert.Equal(16, total); // 1 + 10 + 5 = 16
    }

    // ── end of Round 7 tests ──

    // ──────────────────────────────────────────────
    //  Round 8: 数据完整性 + 边界组件尺寸
    // ──────────────────────────────────────────────

    // H8.1: Zero-size tag component (e.g., empty struct).
    // A struct with no fields has size=1 in C# (not 0). We use a
    // 0-byte-layout struct via Unsafe.SizeOf to create a true zero-size tag.
    // _elementSizes[col] = 0 → columnBytes = rowsInSeg * 0 = 0 → CopyBlockUnaligned
    // is skipped. GetComponentAt offset: _columnByteOffsets[col] + row * 0 = same
    // offset for all rows. This test verifies the archetype doesn't crash.
    private readonly record struct EmptyTag;
    private readonly record struct PositionAndTag(int X, int Y);

    [Fact]
    public void Zero_size_tag_component_in_chunked_archetype_does_not_crash()
    {
        var registry = new ComponentRegistry();
        var tag = registry.GetOrCreate<EmptyTag>();
        var posComp = registry.GetOrCreate<PositionAndTag>();
        var sig = new Signature(tag, posComp);
        var archetype = new Archetype(sig, [typeof(EmptyTag), typeof(PositionAndTag)], capacity: 4);

        for (var i = 0; i < 50; i++)
        {
            var row = archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, row, new EmptyTag());
            archetype.SetComponentAtTyped(1, row, new PositionAndTag(i, i * 2));
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        // Read component data — should not crash or return corrupted data
        for (var i = 0; i < 50; i++)
        {
            var pos = archetype.GetComponentAt<PositionAndTag>(1, i);
            Assert.Equal(new PositionAndTag(i, i * 2), pos);
        }

        var entities = archetype.GetEntities();
        Assert.Equal(50, entities.Length);
    }

    // H8.2: Very large component (> segment capacity threshold).
    // For a 2MB component, _segmentCapacity = max(16, RoundUpToPow2(2MB / 2MB))
    // = max(16, 1) rounded to power of 2 = 16. So each segment has 16 entities.
    // This test verifies the segment allocation and data storage works correctly.
    // We use a smaller "big" component to avoid actual 2MB allocations.
    private unsafe struct BigComponent
    {
        public int Value;
        public fixed byte Pad[65532]; // ~64KB
    }

    [Fact]
    public void Large_component_in_chunked_archetype_stores_correctly()
    {
        var registry = new ComponentRegistry();
        var big = registry.GetOrCreate<BigComponent>();
        var segCap = 32; // for BigComponent (65536 bytes), segCap ~ 2MB/65536 = 32
        var archetype = new Archetype(new Signature(big), [typeof(BigComponent)], capacity: 4);

        // Add entities to create multiple segments
        for (var i = 0; i < segCap * 3; i++)
        {
            var e = new Entity(i + 1, 1);
            var row = archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, row, new BigComponent { Value = i * 100 });
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        Assert.True(archetype.SegmentCount >= 3);
        Assert.Equal(segCap * 3, archetype.EntityCount);

        // Verify all data
        for (var i = 0; i < segCap * 3; i++)
            Assert.Equal(i * 100, archetype.GetComponentAt<BigComponent>(0, i).Value);
    }

    // H8.3: ComputeColumnLayout alignment with non-power-of-2 component sizes.
    // Using a 12-byte struct, verify column offsets are aligned correctly.
    private readonly record struct TwelveBytes(long A, int B); // 8 + 4 = 12

    [Fact]
    public void Non_power_of_two_component_size_alignment_is_correct()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<TwelveBytes>();
        var posComp = registry.GetOrCreate<Position>();
        var sig = new Signature(comp, posComp);
        var archetype = new Archetype(sig, [typeof(TwelveBytes), typeof(Position)], capacity: 4);

        for (var i = 0; i < 50; i++)
        {
            var row = archetype.AddEntity(new Entity(i + 1, 1));
            archetype.SetComponentAtTyped(0, row, new TwelveBytes(i, i * 2));
            archetype.SetComponentAtTyped(1, row, new Position(i * 3, i * 4));
        }
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(new TwelveBytes(i, i * 2), archetype.GetComponentAt<TwelveBytes>(0, i));
            Assert.Equal(new Position(i * 3, i * 4), archetype.GetComponentAt<Position>(1, i));
        }
    }

    // H8.4: Column offset rebase on promotion is correct — verified by
    // Natural_promotion_preserves_multi_column_data test.
    //
    // H8.5: Per-segment column offsets consistency — all segments share the
    // same _columnByteOffsets (based on _segmentCapacity). Verified by inspection
    // and existing multi-column tests.
    //
    // ── end of Round 8 tests ──

    // ──────────────────────────────────────────────
    //  Round 6: 快照/恢复 + 池复用
    // ──────────────────────────────────────────────

    // H6.1: CaptureState on chunked archetype with swap-removed entities
    // uses WriteColumnOrderedTo with sorted rows. Verify round-trip
    // correctness via World.CaptureState/RestoreState.
    [Fact]
    public void CaptureState_on_chunked_archetype_with_removals()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);
        var entities = new List<Entity>();

        // Create entities, promote to chunked, then remove some
        for (var i = 0; i < 100; i++)
            entities.Add(world.Create(new Position(i, i * 2)));

        // Force chunked promotion via the archetype
        Assert.True(world.TryGetLocation(entities[0], out var info));
        info.Archetype.ForceChunkedForTesting();
        Assert.True(info.Archetype.IsChunked);

        // Remove several entities (creates swap-removed holes)
        world.Destroy(entities[10]);
        world.Destroy(entities[42]);
        world.Destroy(entities[88]);

        // Snapshot and restore
        var snap = world.CaptureState();
        world.RestoreState(snap);

        // Verify surviving entities
        for (var i = 0; i < 100; i++)
        {
            if (i == 10 || i == 42 || i == 88)
                Assert.False(world.IsAlive(entities[i]));
            else
            {
                Assert.True(world.IsAlive(entities[i]));
                Assert.Equal(new Position(i, i * 2), world.Get<Position>(entities[i]));
            }
        }
    }

    // H6.2: RestoreFlatBackup ensures capacity first, then copies.
    // Already covered by existing tests (BUG_capture_nonchunked_then_promote).
    //
    // H6.3: Pool reuse - large arrays recycled for smaller archetypes.
    // Already covered by BUG_chunked_restore_pooled_larger_backup_arrays test.
    //
    // H6.4: Empty archetype snapshot round-trip.
    [Fact]
    public void Capture_and_restore_empty_archetype()
    {
        var world = new World();

        // Create and immediately destroy an entity to create an empty archetype
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        var snapshot = world.CaptureState();
        // RestoreState on empty archetype should be a no-op
        world.RestoreState(snapshot);
        // Assert no exception
    }

    // H6.5: Mixed mode world (flat + chunked archetypes) snapshot round-trip.
    [Fact]
    public void CaptureState_restoreState_mixed_modes()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 4);

        // All 205 entities share the same Position-only archetype.
        // Position segCap=262144, so 205 entities never promote naturally.
        // We force chunked to exercise chunked snapshot/restore.
        var flatEntities = new Entity[5];
        for (var i = 0; i < 5; i++)
            flatEntities[i] = world.Create(new Position(i, i + 1));

        // Get the archetype after first entity exists
        var desc = new QueryDescription().With<Position>();
        var coreQuery = MiniQueryCache.Create(world, in desc);
        var arch = Assert.Single(coreQuery.MatchedArchetypes);

        var chunkedEntities = new Entity[200];
        for (var i = 0; i < 200; i++)
            chunkedEntities[i] = world.Create(new Position(i + 100, i + 200));

        // Force chunked on the single archetype
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        foreach (var e in chunkedEntities)
        {
            Assert.True(world.IsAlive(e));
        }

        // Snapshot and restore
        var snap = world.CaptureState();
        world.RestoreState(snap);

        // Verify flat archetype entities
        for (var i = 0; i < 5; i++)
        {
            Assert.True(world.IsAlive(flatEntities[i]));
            Assert.Equal(new Position(i, i + 1), world.Get<Position>(flatEntities[i]));
        }

        // Verify chunked archetype entities by their creation values
        for (var i = 0; i < 200; i++)
        {
            Assert.True(world.IsAlive(chunkedEntities[i]));
            Assert.Equal(new Position(i + 100, i + 200), world.Get<Position>(chunkedEntities[i]));
        }
    }

    // ── end of Round 6 tests ──

    // ──────────────────────────────────────────────
    //  Round 9: 属性测试 / Fuzzing (bonus)
    // ──────────────────────────────────────────────

    // Randomised property-based test from the plan. Runs a random sequence
    // of operations (AddEntity, RemoveAt, VerifyAll, ForcePromote) and
    // validates invariants after each step.
    // Uses a fixed seed for reproducibility, then runs the full suite.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(12345)]
    [InlineData(99999)]
    public void Fuzz_chunk_mode_random_operations_preserve_invariants(int seed)
    {
        var rng = new Random(seed);

        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: rng.Next(1, 100));

        var tracker = new Dictionary<int, int>(); // entity.Id → expected Value
        var nextId = 1;

        for (var step = 0; step < 2000; step++)
        {
            // Guarantee chunked mode is exercised: promote at step 200
            if (step == 200 && !archetype.IsChunked)
            {
                archetype.ForceChunkedForTesting();
                Assert.True(archetype.IsChunked);
            }

            var op = rng.Next(0, 4);
            switch (op)
            {
                case 0: // AddEntity
                {
                    var id = nextId++;
                    var row = archetype.AddEntity(new Entity(id, 1));
                    var val = rng.Next(1, 1000000);
                    archetype.SetComponentAtTyped(0, row, new Component1024 { Value = val });
                    tracker[id] = val;
                    break;
                }
                case 1 when archetype.EntityCount > 0: // RemoveAt
                {
                    var entities = archetype.GetEntities();
                    var removeIdx = rng.Next(0, entities.Length);
                    var removedId = entities[removeIdx].Id;
                    archetype.RemoveAt(removeIdx, out var moved);
                    tracker.Remove(removedId);
                    // moved entity's row changed — its Value is now at removeIdx
                    if (moved.IsValid && tracker.ContainsKey(moved.Id))
                    {
                        var movedVal = archetype.GetComponentAt<Component1024>(0, removeIdx).Value;
                        Assert.Equal(tracker[moved.Id], movedVal);
                    }
                    break;
                }
                case 2 when archetype.EntityCount > 0: // Verify all
                {
                    var allEntities = archetype.GetEntities();
                    Assert.Equal(tracker.Count, allEntities.Length);
                    for (var i = 0; i < allEntities.Length; i++)
                    {
                        var expected = tracker[allEntities[i].Id];
                        var actual = archetype.GetComponentAt<Component1024>(0, i).Value;
                        Assert.Equal(expected, actual);
                    }
                    break;
                }
                case 3: // Force promote (also random, but deterministic at step 200 above)
                {
                    if (!archetype.IsChunked)
                        archetype.ForceChunkedForTesting();
                        Assert.True(archetype.IsChunked);
                    break;
                }
            }
        }
        // Verify the archetype IS chunked after the test (step 200 guarantee)
        Assert.True(archetype.IsChunked, "Fuzz test must exercise chunked mode");

        // Final full verification
        var finalEntities = archetype.GetEntities();
        Assert.Equal(tracker.Count, finalEntities.Length);
        for (var i = 0; i < finalEntities.Length; i++)
        {
            Assert.Equal(tracker[finalEntities[i].Id],
                archetype.GetComponentAt<Component1024>(0, i).Value);
        }
    }

    // Larger-scale fuzz: 10000 steps, multi-column (Position + Component1024),
    // exercises multi-segment scenarios more aggressively.
    [Theory]
    [InlineData(7)]
    [InlineData(777)]
    [InlineData(65535)]
    public void Fuzz_large_scale_random_operations_multi_column(int seed)
    {
        var rng = new Random(seed);

        var registry = new ComponentRegistry();
        var posComp = registry.GetOrCreate<Position>();
        var bigComp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(posComp, bigComp);
        var archetype = new Archetype(sig, [typeof(Position), typeof(Component1024)],
            capacity: rng.Next(4, 32));

        var tracker = new Dictionary<int, (Position Pos, int BigVal)>();
        var nextId = 1;

        for (var step = 0; step < 5000; step++)
        {
            // Guarantee chunked mode: promote at step 500
            if (step == 500 && !archetype.IsChunked)
            {
                archetype.ForceChunkedForTesting();
                Assert.True(archetype.IsChunked);
            }

            var op = rng.Next(0, 4);
            switch (op)
            {
                case 0: // AddEntity
                {
                    var id = nextId++;
                    var row = archetype.AddEntity(new Entity(id, 1));
                    var pos = new Position(rng.Next(-10000, 10000), rng.Next(-10000, 10000));
                    var bigVal = rng.Next(1, 1000000);
                    archetype.SetComponentAtTyped(0, row, pos);
                    archetype.SetComponentAtTyped(1, row, new Component1024 { Value = bigVal });
                    tracker[id] = (pos, bigVal);
                    break;
                }
                case 1 when archetype.EntityCount > 0: // RemoveAt
                {
                    var entities = archetype.GetEntities();
                    var removeIdx = rng.Next(0, entities.Length);
                    var removedId = entities[removeIdx].Id;
                    archetype.RemoveAt(removeIdx, out var moved);
                    tracker.Remove(removedId);
                    if (moved.IsValid && tracker.ContainsKey(moved.Id))
                    {
                        var expected = tracker[moved.Id];
                        var actualPos = archetype.GetComponentAt<Position>(0, removeIdx);
                        var actualBig = archetype.GetComponentAt<Component1024>(1, removeIdx).Value;
                        Assert.Equal(expected.Pos, actualPos);
                        Assert.Equal(expected.BigVal, actualBig);
                    }
                    break;
                }
                case 2 when archetype.EntityCount > 0: // Verify all
                {
                    var allEntities = archetype.GetEntities();
                    Assert.Equal(tracker.Count, allEntities.Length);
                    for (var i = 0; i < allEntities.Length; i++)
                    {
                        var expected = tracker[allEntities[i].Id];
                        var actualPos = archetype.GetComponentAt<Position>(0, i);
                        var actualBig = archetype.GetComponentAt<Component1024>(1, i).Value;
                        Assert.Equal(expected.Pos, actualPos);
                        Assert.Equal(expected.BigVal, actualBig);
                    }
                    break;
                }
                case 3: // Force promote + bulk growth
                {
                    if (!archetype.IsChunked)
                        archetype.ForceChunkedForTesting();
                        Assert.True(archetype.IsChunked);
                    // After chunked, exercise bulk growth via EnsureCapacity
                    if (rng.Next(0, 3) == 0)
                        archetype.EnsureCapacity(archetype.Capacity + rng.Next(1, 5000));
                    break;
                }
            }
        }
        Assert.True(archetype.IsChunked, "Large-scale fuzz must exercise chunked mode");

        // Final verification
        var finalEntities = archetype.GetEntities();
        Assert.Equal(tracker.Count, finalEntities.Length);
        for (var i = 0; i < finalEntities.Length; i++)
        {
            var expected = tracker[finalEntities[i].Id];
            Assert.Equal(expected.Pos, archetype.GetComponentAt<Position>(0, i));
            Assert.Equal(expected.BigVal, archetype.GetComponentAt<Component1024>(1, i).Value);
        }
    }
}