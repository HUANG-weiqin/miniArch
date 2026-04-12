using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class WorldLifecycleTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct C1(int Value);
    private readonly record struct C2(int Value);
    private readonly record struct C3(int Value);
    private readonly record struct C4(int Value);
    private readonly record struct C5(int Value);
    private readonly record struct C6(int Value);
    private readonly record struct C7(int Value);
    private readonly record struct C8(int Value);
    private readonly record struct C9(int Value);
    private readonly record struct C10(int Value);
    private readonly record struct C11(int Value);
    private readonly record struct C12(int Value);
    private readonly record struct C13(int Value);
    private readonly record struct C14(int Value);
    private readonly record struct C15(int Value);
    private readonly record struct C16(int Value);

    [Fact]
    public void Create_returns_a_valid_entity()
    {
        var world = new World();

        var entity = world.Create();

        Assert.True(entity.IsValid);
        Assert.True(world.IsAlive(entity));
        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(entity.Version, info.Version);
    }

    [Fact]
    public void Destroy_recycles_ids_safely()
    {
        var world = new World();
        var first = world.Create();

        world.Destroy(first);
        var second = world.Create();

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public void Destroy_marks_the_entity_not_alive()
    {
        var world = new World();
        var entity = world.Create();

        world.Destroy(entity);

        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void Version_mismatch_makes_stale_entities_invalid()
    {
        var world = new World();
        var first = world.Create();

        world.Destroy(first);
        var second = world.Create();

        Assert.False(world.IsAlive(first));
        Assert.False(world.TryGetLocation(first, out _));
        Assert.True(world.IsAlive(second));
        Assert.True(world.TryGetLocation(second, out _));
    }

    [Fact]
    public void Entity_metadata_points_to_the_current_archetype_and_chunk_position()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Contains(new ComponentType(0), info.Archetype.Signature);
        Assert.Contains(new ComponentType(1), info.Archetype.Signature);
        Assert.Equal(0, info.ChunkIndex);
        Assert.Equal(0, info.RowIndex);
    }

    [Fact]
    public void Create_with_components_places_entity_directly_into_final_archetype_without_intermediate_archetypes()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2), new Velocity(3, 4));
        var positionId = world.Components.GetOrCreate<Position>();
        var velocityId = world.Components.GetOrCreate<Velocity>();

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(2, info.Archetype.Signature.Count);
        Assert.Contains(positionId, info.Archetype.Signature);
        Assert.Contains(velocityId, info.Archetype.Signature);

        var chunk = info.Archetype.GetChunk(info.ChunkIndex);
        Assert.Equal(new Position(1, 2), chunk.GetComponent<Position>(positionId, info.RowIndex));
        Assert.Equal(new Velocity(3, 4), chunk.GetComponent<Velocity>(velocityId, info.RowIndex));

        var positionQuery = world.Query<Position>();
        var matchedArchetypes = positionQuery.MatchedArchetypes;
        Assert.Single(matchedArchetypes);
        Assert.Same(info.Archetype, matchedArchetypes[0]);
    }

    [Fact]
    public void Create_supports_up_to_sixteen_components_without_intermediate_archetypes()
    {
        var world = new World();
        var entity = world.Create(
            new C1(1), new C2(2), new C3(3), new C4(4),
            new C5(5), new C6(6), new C7(7), new C8(8),
            new C9(9), new C10(10), new C11(11), new C12(12),
            new C13(13), new C14(14), new C15(15), new C16(16));
        var c1 = world.Components.GetOrCreate<C1>();
        var c16 = world.Components.GetOrCreate<C16>();

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(16, info.Archetype.Signature.Count);

        var chunk = info.Archetype.GetChunk(info.ChunkIndex);
        Assert.Equal(new C1(1), chunk.GetComponent<C1>(c1, info.RowIndex));
        Assert.Equal(new C16(16), chunk.GetComponent<C16>(c16, info.RowIndex));

        var matchedArchetypes = world.Query<C1>().MatchedArchetypes;
        Assert.Single(matchedArchetypes);
        Assert.Same(info.Archetype, matchedArchetypes[0]);
    }

    [Fact]
    public void EnsureCapacity_grows_entity_storage_before_creation()
    {
        var world = new World();

        world.EnsureCapacity(256);

        Assert.True(world.EntityCapacity >= 256);
    }

    [Fact]
    public void Pre_sized_world_can_create_many_valid_entities()
    {
        var world = new World();
        world.EnsureCapacity(512);

        Entity last = default;
        for (var i = 0; i < 512; i++)
        {
            last = world.Create();
        }

        Assert.Equal(512, world.EntityCapacity);
        Assert.True(last.IsValid);
        Assert.True(world.TryGetLocation(last, out var info));
        Assert.Equal(last.Version, info.Version);
    }

    [Fact]
    public void CreateMany_fills_the_supplied_buffer_with_valid_entities()
    {
        var world = new World();
        var entities = new Entity[8];

        world.CreateMany(entities);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(entities[i].IsValid);
            Assert.Equal(i, entities[i].Id);
            Assert.True(world.TryGetLocation(entities[i], out var info));
            Assert.Equal(entities[i].Version, info.Version);
        }
    }

    [Fact]
    public void CreateMany_preserves_location_order_inside_the_empty_archetype()
    {
        var world = new World();
        var entities = new Entity[16];

        world.CreateMany(entities);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.TryGetLocation(entities[i], out var info));
            Assert.Equal(0, info.ChunkIndex);
            Assert.Equal(i, info.RowIndex);
        }
    }

    [Fact]
    public void CreateMany_preserves_chunk_and_row_progression_across_chunk_boundaries()
    {
        var world = new World(chunkCapacity: 4);
        var entities = new Entity[10];

        world.CreateMany(entities);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.TryGetLocation(entities[i], out var info));
            Assert.Equal(i / 4, info.ChunkIndex);
            Assert.Equal(i % 4, info.RowIndex);
        }
    }

    [Fact]
    public void CreateMany_appends_after_existing_empty_archetype_entities()
    {
        var world = new World(chunkCapacity: 4);
        var firstBatch = new Entity[6];
        var secondBatch = new Entity[5];

        world.CreateMany(firstBatch);
        world.CreateMany(secondBatch);

        for (var i = 0; i < firstBatch.Length; i++)
        {
            Assert.True(world.TryGetLocation(firstBatch[i], out var info));
            Assert.Equal(i / 4, info.ChunkIndex);
            Assert.Equal(i % 4, info.RowIndex);
        }

        for (var i = 0; i < secondBatch.Length; i++)
        {
            Assert.True(world.TryGetLocation(secondBatch[i], out var info));
            var absoluteIndex = firstBatch.Length + i;
            Assert.Equal(absoluteIndex / 4, info.ChunkIndex);
            Assert.Equal(absoluteIndex % 4, info.RowIndex);
        }
    }

    [Fact]
    public void CreateMany_reuses_destroyed_ids_with_incremented_versions()
    {
        var world = new World(chunkCapacity: 4);
        var firstBatch = new Entity[6];
        world.CreateMany(firstBatch);

        for (var i = 0; i < firstBatch.Length; i++)
        {
            world.Destroy(firstBatch[i]);
        }

        var recycledBatch = new Entity[6];
        world.CreateMany(recycledBatch);

        var ids = recycledBatch.Select(entity => entity.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, ids);

        for (var i = 0; i < firstBatch.Length; i++)
        {
            Assert.False(world.TryGetLocation(firstBatch[i], out _));
        }

        foreach (var entity in recycledBatch)
        {
            Assert.Equal(1, entity.Version);
            Assert.True(world.TryGetLocation(entity, out var info));
            Assert.Equal(entity.Version, info.Version);
        }
    }

    [Fact]
    public void CreateMany_reuses_available_ids_before_allocating_new_ones()
    {
        var world = new World(chunkCapacity: 4);
        var firstBatch = new Entity[6];
        world.CreateMany(firstBatch);

        world.Destroy(firstBatch[1]);
        world.Destroy(firstBatch[4]);

        var secondBatch = new Entity[4];
        world.CreateMany(secondBatch);

        var ids = secondBatch.Select(entity => entity.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { 1, 4, 6, 7 }, ids);

        foreach (var entity in secondBatch.Where(entity => entity.Id is 1 or 4))
        {
            Assert.Equal(1, entity.Version);
            Assert.True(world.TryGetLocation(entity, out _));
        }

        foreach (var entity in secondBatch.Where(entity => entity.Id >= 6))
        {
            Assert.Equal(0, entity.Version);
            Assert.True(world.TryGetLocation(entity, out _));
        }
    }

    [Fact]
    public void CreateMany_mixed_ids_reuses_available_rows_before_appending_new_capacity()
    {
        var world = new World(chunkCapacity: 4);
        var firstBatch = new Entity[6];
        world.CreateMany(firstBatch);

        world.Destroy(firstBatch[1]);
        world.Destroy(firstBatch[4]);

        var secondBatch = new Entity[4];
        world.CreateMany(secondBatch);

        Assert.Equal(4, secondBatch[0].Id);
        Assert.Equal(1, secondBatch[1].Id);
        Assert.Equal(6, secondBatch[2].Id);
        Assert.Equal(7, secondBatch[3].Id);

        Assert.True(world.TryGetLocation(secondBatch[0], out var firstReused));
        Assert.Equal(1, firstReused.ChunkIndex);
        Assert.Equal(1, firstReused.RowIndex);

        Assert.True(world.TryGetLocation(secondBatch[1], out var secondReused));
        Assert.Equal(1, secondReused.ChunkIndex);
        Assert.Equal(2, secondReused.RowIndex);

        Assert.True(world.TryGetLocation(secondBatch[2], out var firstFresh));
        Assert.Equal(1, firstFresh.ChunkIndex);
        Assert.Equal(3, firstFresh.RowIndex);

        Assert.True(world.TryGetLocation(secondBatch[3], out var secondFresh));
        Assert.Equal(0, secondFresh.ChunkIndex);
        Assert.Equal(3, secondFresh.RowIndex);
    }

    [Fact]
    public void Default_world_scales_chunk_capacity_for_dense_component_archetypes()
    {
        var world = new World();

        for (var i = 0; i < 256; i++)
        {
            world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
        }

        var query = world.Query<Position, Velocity>();

        Assert.Single(query.MatchedArchetypes);
        Assert.Equal(1, query.GetChunkSpan().Length);
    }

    [Fact]
    public void Explicit_chunk_capacity_keeps_fixed_chunk_boundaries()
    {
        var world = new World(chunkCapacity: 4);

        for (var i = 0; i < 5; i++)
        {
            world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
        }

        var query = world.Query<Position, Velocity>();

        Assert.Single(query.MatchedArchetypes);
        Assert.Equal(2, query.GetChunkSpan().Length);
    }

    [Fact]
    public void Link_stores_parent_relationship_and_children_list()
    {
        var world = new World();
        var parent = world.Create();
        var firstChild = world.Create();
        var secondChild = world.Create();

        world.Link(parent, firstChild);
        world.Link(parent, secondChild);

        Assert.True(world.TryGetParent(firstChild, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);

        var children = world.GetChildren(parent);
        Assert.Equal(2, children.Count);
        Assert.Equal([firstChild, secondChild], children.OrderBy(entity => entity.Id).ToArray());
    }

    [Fact]
    public void Link_reparents_child_to_latest_parent()
    {
        var world = new World();
        var firstParent = world.Create();
        var secondParent = world.Create();
        var child = world.Create();

        world.Link(firstParent, child);
        world.Link(secondParent, child);

        Assert.True(world.TryGetParent(child, out var resolvedParent));
        Assert.Equal(secondParent, resolvedParent);
        Assert.Empty(world.GetChildren(firstParent));
        Assert.Equal([child], world.GetChildren(secondParent));
    }

    [Fact]
    public void Destroy_parent_cascades_to_all_descendants()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();

        world.Link(root, child);
        world.Link(child, grandChild);

        world.Destroy(root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandChild));
        Assert.Empty(world.GetChildren(root));
    }

    [Fact]
    public void Reused_entity_slot_does_not_inherit_destroyed_relationship()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();

        world.Link(parent, child);
        world.Destroy(child);

        var replacement = world.Create();

        Assert.Equal(child.Id, replacement.Id);
        Assert.NotEqual(child.Version, replacement.Version);
        Assert.False(world.TryGetParent(replacement, out _));
        Assert.Empty(world.GetChildren(parent));
    }
}
