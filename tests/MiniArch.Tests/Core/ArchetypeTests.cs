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

        var startRow = archetype.ReserveRows(10);
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
        Assert.True(archetype.IsChunked);
        Assert.Equal(1, archetype.EntityCount);
        Assert.Equal(1, archetype.SegmentCount);

        // Position = 8 bytes, segment capacity = 2MB / 8 = 262144
        // Different component sizes produce different segment capacities
        Assert.Equal(262144, archetype.Capacity);
    }

}
