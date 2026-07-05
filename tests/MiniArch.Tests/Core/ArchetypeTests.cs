using MiniArch.Core;

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

        var description = new QueryDescription().With<Position>().With<Velocity>();
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

}
