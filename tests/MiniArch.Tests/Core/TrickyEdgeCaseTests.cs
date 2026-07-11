using System.Reflection;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class TrickyEdgeCaseTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    // ══════════════════════════════════════════════════════—
    // Category 1: Entity Handle / Lifecycle Edge Cases
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void Stale_handle_Set_throws_because_version_check_always_runs()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2));

        world.Destroy(original);
        var recycled = world.Create(new Position(3, 4));

        Assert.Equal(original.Id, recycled.Id);
        Assert.NotEqual(original.Version, recycled.Version);

        Assert.Throws<InvalidOperationException>(() => world.Set(original, new Position(99, 99)));

        Assert.True(world.TryGet(recycled, out Position p));
        Assert.Equal(new Position(3, 4), p);
    }

    [Fact]
    public void IsAlive_returns_false_for_entity_from_different_world()
    {
        var worldA = new World();
        var worldB = new World();
        var entityA = worldA.Create(new Position(1, 2));

        Assert.False(worldB.IsAlive(entityA));
    }

    [Fact]
    public void TryGet_returns_false_for_entity_from_different_world()
    {
        var worldA = new World();
        var worldB = new World();
        var entityA = worldA.Create(new Position(1, 2));

        Assert.False(worldB.TryGet(entityA, out Position _));
    }

    [Fact]
    public void Destroy_and_recreate_cycle_preserves_correct_versions_across_many_iterations()
    {
        var world = new World();

        for (var cycle = 0; cycle < 100; cycle++)
        {
            var entity = world.CreateEmpty();
            // Create→Destroy in a tight loop always reuses slot 0 (freed
            // immediately each iteration) with a monotonically increasing
            // version. Verifying both catches ABA-style recycling bugs.
            Assert.Equal(0, entity.Id);
            Assert.Equal(cycle + 1, entity.Version);
            Assert.True(world.IsAlive(entity));
            world.Destroy(entity);
            Assert.False(world.IsAlive(entity));
        }
    }

    [Fact]
    public void Create_many_then_selective_destroy_then_create_single_verifies_free_list_correctness()
    {
        var world = new World(chunkCapacity: 4, entityCapacity: 100);
        var batch = new Entity[100];
        for (var i = 0; i < batch.Length; i++)
        {
            batch[i] = world.CreateEmpty();
        }

        var destroyed = new HashSet<int>();
        for (var i = 0; i < batch.Length; i += 2)
        {
            world.Destroy(batch[i]);
            destroyed.Add(batch[i].Id);
        }

        var recreated = new List<Entity>();
        for (var i = 0; i < 50; i++)
        {
            recreated.Add(world.CreateEmpty());
        }

        var reuseIds = recreated.Where(e => destroyed.Contains(e.Id)).ToList();
        var freshIds = recreated.Where(e => !destroyed.Contains(e.Id)).ToList();

        foreach (var entity in reuseIds)
        {
            Assert.Equal(2, entity.Version);
            Assert.True(world.IsAlive(entity));
        }

        foreach (var entity in freshIds)
        {
            Assert.Equal(1, entity.Version);
            Assert.True(world.IsAlive(entity));
        }

        foreach (var entity in batch)
        {
            if (destroyed.Contains(entity.Id) && !recreated.Any(e => e.Id == entity.Id))
            {
                Assert.False(world.IsAlive(entity));
            }
        }
    }

    // ══════════════════════════════════════════════════════—
    // Category 2: Set / Add / Remove Edge Cases
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void Set_default_value_preserves_integrity()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        var positionId = ComponentRegistry.Shared.GetOrCreate<Position>();

        world.Set(entity, new Position(0, 0));

        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(0, 0), p);

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(new Position(0, 0), info.Archetype.GetComponentAt<Position>(info.Archetype.GetComponentIndex(positionId), info.RowIndex));
    }

    [Fact]
    public void Add_component_that_already_exists_throws()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        world.Add(entity, new Position(1, 2));
        Assert.Throws<InvalidOperationException>(() => world.Add(entity, new Position(99, 99)));
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(1, 2), p); // unchanged
    }

    [Fact]
    public void Remove_then_Add_readds_component()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        world.Remove<Position>(entity);
        Assert.False(world.TryGet<Position>(entity, out _));

        world.Add(entity, new Position(5, 6));

        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(5, 6), p);
    }

    [Fact]
    public void Rapid_add_remove_cycling_across_archetypes_does_not_corrupt_state()
    {
        var world = new World();
        var entity = world.CreateEmpty();
        var positionId = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityId = ComponentRegistry.Shared.GetOrCreate<Velocity>();
        var healthId = ComponentRegistry.Shared.GetOrCreate<Health>();

        for (var cycle = 0; cycle < 100; cycle++)
        {
            world.Add(entity, new Position(cycle, cycle + 1));
            Assert.True(HasComponent(world, entity, positionId));

            world.Add(entity, new Velocity(cycle + 2, cycle + 3));
            Assert.True(HasComponent(world, entity, velocityId));

            world.Remove<Velocity>(entity);
            Assert.False(HasComponent(world, entity, velocityId));

            world.Add(entity, new Health(cycle + 4));
            Assert.True(HasComponent(world, entity, healthId));

            world.Remove<Position>(entity);
            Assert.False(HasComponent(world, entity, positionId));

            world.Remove<Health>(entity);
            Assert.False(HasComponent(world, entity, healthId));
        }

        Assert.True(world.IsAlive(entity));
        Assert.True(world.TryGetLocation(entity, out var finalLocation));
        Assert.Equal(0, finalLocation.Archetype.Signature.Count);
    }

    [Fact]
    public void Remove_nonexistent_component_is_silent_noop()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        world.Remove<Velocity>(entity);
        world.Remove<Health>(entity);
        world.Remove<Velocity>(entity);

        Assert.True(world.IsAlive(entity));
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(1, 2), p);
        Assert.False(world.TryGet<Velocity>(entity, out _));
    }

    [Fact]
    public void Set_preserves_unchanged_components_after_multiple_Sets_on_different_types()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2), new Velocity(3, 4), new Health(100));
        var positionId = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityId = ComponentRegistry.Shared.GetOrCreate<Velocity>();
        var healthId = ComponentRegistry.Shared.GetOrCreate<Health>();

        world.Set(entity, new Position(10, 20));
        world.Set(entity, new Health(200));

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(new Position(10, 20), info.Archetype.GetComponentAt<Position>(info.Archetype.GetComponentIndex(positionId), info.RowIndex));
        Assert.Equal(new Velocity(3, 4), info.Archetype.GetComponentAt<Velocity>(info.Archetype.GetComponentIndex(velocityId), info.RowIndex));
        Assert.Equal(new Health(200), info.Archetype.GetComponentAt<Health>(info.Archetype.GetComponentIndex(healthId), info.RowIndex));
    }

    [Fact]
    public void Set_entity_stability_after_many_sequential_same_value_overwrites()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        Assert.True(world.TryGetLocation(entity, out var before));

        for (var i = 0; i < 1000; i++)
        {
            world.Set(entity, new Position(42, 42));
        }

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.Same(before.Archetype, after.Archetype);
        Assert.Equal(before.RowIndex, after.RowIndex);
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(42, 42), p);
    }

    // ══════════════════════════════════════════════════════—
    // Category 3: Query Edge Cases
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void QueryDescription_order_independent_equality()
    {
        var desc1 = new QueryDescription().With<Position>().With<Velocity>().Without<Health>();
        var desc2 = new QueryDescription().Without<Health>().With<Velocity>().With<Position>();

        Assert.Equal(desc1, desc2);
        Assert.Equal(desc1.GetHashCode(), desc2.GetHashCode());
    }

    [Fact]
    public void QueryDescription_different_orders_same_WithAny_are_equal()
    {
        var d1 = new QueryDescription().With<Position>().WithAny<Velocity>().WithAny<Health>();
        var d2 = new QueryDescription().With<Position>().WithAny<Health>().WithAny<Velocity>();

        Assert.Equal(d1, d2);
    }

    [Fact]
    public void Self_contradictory_query_returns_no_entities()
    {
        var world = new World();
        world.Create(new Position(1, 2));
        var description = new QueryDescription().With<Position>().Without<Position>();
        var query = MiniQueryCache.Create(world, in description);

        Assert.Empty(query.MatchedArchetypes);
        Assert.Empty(query.MatchedArchetypes);
    }

    [Fact]
    public void Query_with_only_WithAny_matches_nothing_when_entities_have_no_components()
    {
        var world = new World();
        world.CreateEmpty();
        var description = new QueryDescription().WithAny<Position>();
        var query = MiniQueryCache.Create(world, in description);

        Assert.Empty(query.MatchedArchetypes);
    }

    [Fact]
    public void Query_with_only_WithAny_returns_matching_entities()
    {
        var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Velocity(3, 4));
        world.Create(new Position(5, 6), new Velocity(7, 8));
        world.CreateEmpty(); // empty entity, should not match
        var description = new QueryDescription().WithAny<Position>();
        var query = MiniQueryCache.Create(world, in description);

        Assert.Equal(2, CountEntities(query));
    }

    [Fact]
    public void Default_foreach_entity_count_matches_advanced_chunk_count()
    {
        var world = new World(chunkCapacity: 3);
        for (var i = 0; i < 10; i++)
        {
            var entity = world.CreateEmpty();
            if (i % 2 == 0) world.Add(entity, new Position(i, i));
            if (i % 3 == 0) world.Add(entity, new Velocity(i, i));
        }

        var description = new QueryDescription().With<Position>();
        var defaultQuery = world.Query(in description);
        var advancedQuery = MiniQueryCache.Create(world, in description);

        var defaultCount = 0;
        foreach (var _ in defaultQuery) defaultCount++;

        var advancedCount = CountEntities(advancedQuery);

        Assert.Equal(advancedCount, defaultCount);
        Assert.True(defaultCount > 0);
    }

    [Fact]
    public void Query_after_all_matching_entities_destroyed_still_shows_empty_chunks()
    {
        var world = new World();
        var entities = new Entity[5];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position(i, i));
        }

        var description = new QueryDescription().With<Position>();
        var query = MiniQueryCache.Create(world, in description);
        Assert.Equal(entities.Length, CountEntities(query));

        foreach (var entity in entities) world.Destroy(entity);

        Assert.Equal(0, CountEntities(query));
        // The matching archetype's chunk remains in the snapshot even when empty.
        // This is harmless: all iterator paths skip Count == 0 chunks.
        Assert.Single(query.MatchedArchetypes);
        Assert.Equal(0, query.MatchedArchetypes[0].EntityCount);
    }

    [Fact]
    public void Query_across_chunk_boundaries_integrity()
    {
        var world = new World(chunkCapacity: 4);
        var entities = new Entity[10];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position(i, i + 1));
        }

        var description = new QueryDescription().With<Position>();
        var query = MiniQueryCache.Create(world, in description);

        Assert.Equal(entities.Length, CountEntities(query));

        var seen = new List<Entity>();
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            for (var row = 0; row < archetype.EntityCount; row++)
            {
                seen.Add(archetype.GetEntity(row));
            }
        }

        Assert.Equal(entities.OrderBy(e => e.Id).ToArray(), seen.OrderBy(e => e.Id).ToArray());
    }

    [Fact]
    public void Reused_description_query_cache_invalidates_after_world_mutation()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var query1 = MiniQueryCache.Create(world, in description);
        Assert.Empty(query1.MatchedArchetypes);

        world.Create(new Position(1, 2));
        var query2 = MiniQueryCache.Create(world, in description);

        Assert.Same(query1, query2);
        Assert.Single(query2.MatchedArchetypes);
    }

    // ══════════════════════════════════════════════════════—
    // Category 4: Hierarchy Edge Cases
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void EnumerateChildren_on_entity_with_no_children_returns_empty()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        var children = world.EnumerateChildren(entity).ToChildList();

        Assert.NotNull(children);
        Assert.Empty(children);
    }

    [Fact]
    public void TryGetParent_on_root_entity_returns_false()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        Assert.False(world.TryGetParent(entity, out _));
    }

    [Fact]
    public void Destroy_child_then_recreate_entity_in_same_slot_should_not_inherit_parent()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();

        world.AddChild(parent, child);
        world.Destroy(child);

        var recycled = world.CreateEmpty();
        Assert.Equal(child.Id, recycled.Id);
        Assert.NotEqual(child.Version, recycled.Version);

        Assert.False(world.TryGetParent(recycled, out _));
        Assert.False(world.HasChildren(parent));
    }

    [Fact]
    public void Destroy_entity_with_children_and_components_cleans_up_everything()
    {
        var world = new World();
        var root = world.CreateEmpty();
        var child = world.Create(new Position(1, 2), new Velocity(3, 4));

        world.AddChild(root, child);
        world.Destroy(root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.TryGet<Position>(child, out _));
        Assert.False(world.TryGet<Velocity>(child, out _));
    }

    [Fact]
    public void Destroy_child_keeps_parent_and_siblings_alive()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.CreateEmpty();
        var child2 = world.CreateEmpty();
        var child3 = world.CreateEmpty();

        world.AddChild(parent, child1);
        world.AddChild(parent, child2);
        world.AddChild(parent, child3);

        world.Destroy(child2);

        Assert.True(world.IsAlive(parent));
        Assert.True(world.IsAlive(child1));
        Assert.False(world.IsAlive(child2));
        Assert.True(world.IsAlive(child3));

        var remainingChildren = world.EnumerateChildren(parent).ToChildList();
        Assert.Equal(2, remainingChildren.Count);
        Assert.Contains(child1, remainingChildren);
        Assert.Contains(child3, remainingChildren);
    }



    // ══════════════════════════════════════════════════════—
    // Category 6: Structural Integrity / Chunk Boundaries
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void Entities_spread_across_chunk_boundaries_preserve_set_values()
    {
        var world = new World(chunkCapacity: 3);
        var entities = new Entity[10];
        var expectedValues = new Dictionary<int, Position>();

        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position(i, i + 1));
            expectedValues[entities[i].Id] = new Position(i, i + 1);
        }

        for (var i = 0; i < entities.Length; i += 2)
        {
            world.Set(entities[i], new Position(i * 10, (i + 1) * 10));
            expectedValues[entities[i].Id] = new Position(i * 10, (i + 1) * 10);
        }

        foreach (var entity in entities)
        {
            Assert.True(world.TryGet(entity, out Position p));
            Assert.Equal(expectedValues[entity.Id], p);
        }
    }

    [Fact]
    public void Chunk_span_contracts_after_entity_removal()
    {
        var world = new World(chunkCapacity: 4);
        var entities = new Entity[4];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position(i, i));
        }

        var description = new QueryDescription().With<Position>();
        var query = MiniQueryCache.Create(world, in description);
        Assert.Equal(entities.Length, CountEntities(query));

        world.Remove<Position>(entities[0]);

        var remaining = new List<Entity>();
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            Assert.True(archetype.EntityCount > 0);
            for (var row = 0; row < archetype.EntityCount; row++)
            {
                remaining.Add(archetype.GetEntity(row));
            }
        }

        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public void Entity_migration_between_chunks_preserves_components()
    {
        var world = new World(chunkCapacity: 2);
        var a = world.Create(new Position(1, 2));
        var b = world.Create(new Position(3, 4));
        var c = world.Create(new Position(5, 6));

        Assert.True(world.TryGet(a, out Position pa));
        Assert.True(world.TryGet(b, out Position pb));
        Assert.True(world.TryGet(c, out Position pc));
        Assert.Equal(new Position(1, 2), pa);
        Assert.Equal(new Position(3, 4), pb);
        Assert.Equal(new Position(5, 6), pc);

        world.Remove<Position>(a);
        world.Add(a, new Position(10, 20));

        Assert.True(world.TryGet(a, out Position pa2));
        Assert.Equal(new Position(10, 20), pa2);
        Assert.True(world.TryGet(b, out Position pb2));
        Assert.Equal(new Position(3, 4), pb2);
    }

    [Fact]
    public void Archetype_with_entity_creation_and_removal_leaves_empty_archetype_visible_to_query()
    {
        var world = new World();
        var entity = world.CreateEmpty();
        world.Add(entity, new Position(1, 2));
        world.Remove<Position>(entity);

        var description = new QueryDescription().With<Position>();
        var query = MiniQueryCache.Create(world, in description);

        Assert.NotEmpty(query.MatchedArchetypes);
        Assert.Equal(0, CountEntities(query));
    }

    // ══════════════════════════════════════════════════════—
    // Category 7: World / Snapshot / Replay
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void Replay_empty_delta_does_nothing()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        var delta = new FrameDelta();

        new CommandStream(world).Replay(delta);

        Assert.True(world.IsAlive(entity));
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(1, 2), p);
    }

    [Fact]
    public void Multiple_worlds_operate_independently()
    {
        var worldA = new World();
        var worldB = new World();

        var entityA = worldA.Create(new Position(1, 2));
        var entityB = worldB.Create(new Position(3, 4));

        Assert.True(worldA.TryGet(entityA, out Position pa));
        Assert.True(worldB.TryGet(entityB, out Position pb));

        worldA.Set(entityA, new Position(10, 20));

        Assert.True(worldA.TryGet(entityA, out Position pa2));
        Assert.Equal(new Position(10, 20), pa2);
        Assert.True(worldB.TryGet(entityB, out Position pb2));
        Assert.Equal(new Position(3, 4), pb2);

        var entityALater = worldA.Create(new Position(100, 200));
        Assert.False(worldB.TryGet<Position>(entityALater, out _));
    }

    [Fact]
    public void World_EnsureCapacity_zero_is_valid()
    {
        var world = new World();
        world.EnsureCapacity(0);

        var entity = world.Create(new Position(1, 2));
        Assert.True(world.IsAlive(entity));
    }

    [Fact]
    public void World_chunkCapacity_one_works_correctly()
    {
        var world = new World(chunkCapacity: 1);
        var entities = new Entity[10];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position(i, i + 1));
        }

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.TryGet(entities[i], out Position p));
            Assert.Equal(new Position(i, i + 1), p);
        }

        var description = new QueryDescription().With<Position>();
        var query = MiniQueryCache.Create(world, in description);
        Assert.Equal(1, query.GetArchetypeSpan().Length);
    }

    // ══════════════════════════════════════════════════════—
    // Category 8: TryGet / OrderByEntityId / Query Behaviors
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void TryGet_on_destroyed_entity_returns_false()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        world.Destroy(entity);

        Assert.False(world.TryGet(entity, out Position _));
        Assert.False(world.TryGet(entity, out Velocity _));
    }

    [Fact]
    public void TryGet_on_entity_without_requested_component_returns_false()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        Assert.False(world.TryGet<Velocity>(entity, out _));
        Assert.False(world.TryGet<Health>(entity, out _));
    }

    [Fact]
    public void OrderByEntityId_returns_sorted_entities()
    {
        var world = new World();
        var entities = new Entity[10];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position(i, i));
        }

        var description = new QueryDescription().With<Position>();
        var orderedQuery = world.Query(in description).OrderByEntityIdDescending();

        var seen = new List<Entity>();
        foreach (var entity in orderedQuery)
        {
            seen.Add(entity);
        }

        Assert.Equal(entities.Length, seen.Count);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i - 1].Id >= seen[i].Id);
        }
    }

    [Fact]
    public void Two_ordered_entity_enumerations_are_independent()
    {
        var world = new World();
        for (var i = 0; i < 5; i++)
            world.Create(new Position(i, i));

        var description = new QueryDescription().With<Position>();
        var orderedQuery = world.Query(in description).OrderByEntityId();

        var first = new List<Entity>();
        foreach (var entity in orderedQuery) first.Add(entity);

        var second = new List<Entity>();
        foreach (var entity in orderedQuery) second.Add(entity);

        Assert.Equal(first, second);
    }

    [Fact]
    public void OrderByEntityId_cache_invalidates_on_entity_creation()
    {
        var world = new World();
        for (var i = 0; i < 5; i++)
            world.Create(new Position(i, i));

        var description = new QueryDescription().With<Position>();
        var orderedQuery = world.Query(in description).OrderByEntityId();

        // First enumeration — primes the cache
        var firstPass = new List<Entity>();
        foreach (var e in orderedQuery) firstPass.Add(e);

        // Add more entities — cache must invalidate (count changes)
        for (var i = 0; i < 5; i++)
            world.Create(new Position(i + 100, 0));

        // Second enumeration — should see all 10 entities in order
        var secondPass = new List<Entity>();
        foreach (var e in orderedQuery) secondPass.Add(e);

        Assert.Equal(10, secondPass.Count);
        for (var i = 1; i < secondPass.Count; i++)
            Assert.True(secondPass[i - 1].Id <= secondPass[i].Id);
    }

    [Fact]
    public void OrderByEntityId_cache_invalidates_on_entity_destruction()
    {
        var world = new World();
        var entities = new Entity[10];
        for (var i = 0; i < 10; i++)
            entities[i] = world.Create(new Position(i, i));

        var description = new QueryDescription().With<Position>();
        var orderedQuery = world.Query(in description).OrderByEntityId();

        // First enumeration — primes the cache
        var firstPass = new List<Entity>();
        foreach (var e in orderedQuery) firstPass.Add(e);
        Assert.Equal(10, firstPass.Count);

        // Destroy the first 3 entities — cache must invalidate
        for (var i = 0; i < 3; i++)
            world.Destroy(entities[i]);

        // Second enumeration — should see 7 entities
        var secondPass = new List<Entity>();
        foreach (var e in orderedQuery) secondPass.Add(e);

        Assert.Equal(7, secondPass.Count);
        for (var i = 1; i < secondPass.Count; i++)
            Assert.True(secondPass[i - 1].Id <= secondPass[i].Id);

        // Verify destroyed entities are gone
        foreach (var e in secondPass)
            Assert.True(e.Id >= 3);
    }

    [Fact]
    public void Query_Advanced_property_returns_valid_core_query()
    {
        var world = new World();
        world.Create(new Position(1, 2));
        var description = new QueryDescription().With<Position>();

        var defaultQuery = world.Query(in description);
        Assert.True(defaultQuery.GetChunks().GetEnumerator().MoveNext());
    }

    // ══════════════════════════════════════════════════════—
    // Category 9: ComponentType / Registry
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void Same_struct_type_registered_multiple_times_returns_same_ComponentType()
    {
        var registry = ComponentRegistry.Shared;
        var ct1 = registry.GetOrCreate<Position>();
        var ct2 = registry.GetOrCreate<Position>();

        Assert.Equal(ct1, ct2);
        Assert.Equal(ct1.Value, ct2.Value);
    }

    [Fact]
    public void Different_worlds_share_ComponentRegistry_but_not_entities()
    {
        var worldA = new World();
        var worldB = new World();

        var entityA = worldA.Create(new Position(1, 2));
        var ctA = ComponentRegistry.Shared.GetOrCreate<Position>();
        var ctB = ComponentRegistry.Shared.GetOrCreate<Position>();

        Assert.Equal(ctA, ctB);

        Assert.True(worldA.TryGet(entityA, out Position _));
        Assert.False(worldB.TryGet(entityA, out Position _));
    }

    // Minimal handler for dispose test
    private struct DestructiveWatchHandler : IChangeHandler<Position>, ITransitionHandler
    {
        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue) { }
        public void OnChange(World world, Entity entity, TransitionKind kind) { }
    }

    // ══════════════════════════════════════════════════════—
    // Helpers
    // ══════════════════════════════════════════════════════—

    private static int CountEntities(MiniQueryCache query)
    {
        var total = 0;
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            total += archetype.EntityCount;
        }

        return total;
    }

    private static bool HasComponent(World world, Entity entity, ComponentType type)
    {
        return world.TryGetLocation(entity, out var info) && info.Archetype.Signature.Contains(type);
    }

#if DEBUG
    // ══════════════════════════════════════════════════════—
    // Category: Debug-only safety checks
    // ══════════════════════════════════════════════════════—

    [Fact]
    public void Set_with_negative_entity_id_throws_in_debug()
    {
        var world = new World();
        var badEntity = new Entity(-1, 1);

        Assert.Throws<InvalidOperationException>(() => world.Set(badEntity, new Position(1, 2)));
    }

    [Fact]
    public void Set_with_out_of_range_entity_id_throws_in_debug()
    {
        var world = new World();
        var badEntity = new Entity(9999, 1);

        Assert.Throws<InvalidOperationException>(() => world.Set(badEntity, new Position(1, 2)));
    }

    [Fact]
    public void Add_with_out_of_range_entity_id_throws_in_debug()
    {
        var world = new World();
        var badEntity = new Entity(9999, 1);

        Assert.Throws<InvalidOperationException>(() => world.Add(badEntity, new Position(1, 2)));
    }

    [Fact]
    public void Destroy_with_out_of_range_entity_id_throws_in_debug()
    {
        var world = new World();
        var badEntity = new Entity(9999, 1);

        Assert.Throws<InvalidOperationException>(() => world.Destroy(badEntity));
    }

    [Fact]
    public void Get_with_negative_entity_id_throws_in_debug()
    {
        var world = new World();
        world.Create(new Position(1, 2));
        var badEntity = new Entity(-1, 1);

        Assert.Throws<InvalidOperationException>(() => world.Get<Position>(badEntity));
    }

    [Fact]
    public void Get_with_out_of_range_entity_id_throws_in_debug()
    {
        var world = new World();
        var badEntity = new Entity(9999, 1);

        Assert.Throws<InvalidOperationException>(() => world.Get<Position>(badEntity));
    }

    [Fact]
    public void Get_with_destroyed_entity_throws_in_debug()
    {
        // Liveness check (commit 46dbc0a): a stale handle whose slot has been
        // recycled to a new version must throw, not silently read garbage.
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.Throws<InvalidOperationException>(() => world.Get<Position>(e));
    }

    [Fact]
    public void Get_with_recycled_entity_throws_in_debug()
    {
        // After destroy+recreate the slot is occupied again but with a new version;
        // the stale handle must still throw.
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);
        world.Create(new Position(3, 4)); // recycles id 0 at version 2

        Assert.Throws<InvalidOperationException>(() => world.Get<Position>(e));
    }

    [Fact]
    public void GetRef_with_negative_entity_id_throws_in_debug()
    {
        var world = new World();
        world.Create(new Position(1, 2));
        var badEntity = new Entity(-1, 1);

        Assert.Throws<InvalidOperationException>(() => world.GetRef<Position>(badEntity));
    }

    [Fact]
    public void GetRef_with_out_of_range_entity_id_throws_in_debug()
    {
        var world = new World();
        var badEntity = new Entity(9999, 1);

        Assert.Throws<InvalidOperationException>(() => world.GetRef<Position>(badEntity));
    }

    [Fact]
    public void GetRef_with_destroyed_entity_throws_in_debug()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        Assert.Throws<InvalidOperationException>(() => world.GetRef<Position>(e));
    }

    [Fact]
    public void GetRef_with_recycled_entity_throws_in_debug()
    {
        var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);
        world.Create(new Position(3, 4));

        Assert.Throws<InvalidOperationException>(() => world.GetRef<Position>(e));
    }
#endif

    [Fact]
    public void TryGet_with_out_of_range_entity_id_returns_false()
    {
        var world = new World();
        var badEntity = new Entity(9999, 1);

        Assert.False(world.TryGet(badEntity, out Position _));
    }

    [Fact]
    public void Has_returns_false_for_invalid_entity_handles()
    {
        var world = new World();
        var alive = world.Create(new Position(1, 2));
        world.Destroy(alive);

        Assert.False(world.Has<Position>(default));
        Assert.False(world.Has<Position>(new Entity(-1, 1)));
        Assert.False(world.Has<Position>(new Entity(9999, 1)));
        Assert.False(world.Has<Position>(alive));
    }

    [Fact]
    public void Has_returns_false_for_entity_from_different_world()
    {
        var worldA = new World();
        var worldB = new World();
        var entityA = worldA.Create(new Position(1, 2));

        Assert.False(worldB.Has<Position>(entityA));
    }

#if DEBUG
    [Fact]
    public void Disposed_world_methods_throw_ObjectDisposedException()
    {
        var world = new World();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.AddChild(default, default));
        Assert.Throws<ObjectDisposedException>(() => world.RemoveChild(default));
        Assert.Throws<ObjectDisposedException>(() => world.Has<Position>(default));
        Assert.Throws<ObjectDisposedException>(() => world.TryGetParent(default, out _));
        Assert.Throws<ObjectDisposedException>(() => world.Watch<Position, DestructiveWatchHandler>());
        Assert.Throws<ObjectDisposedException>(() => world.Watch<DestructiveWatchHandler>(new QueryDescription().With<Position>()));
    }
#endif

    // ══════════════════════════════════════════════════════—
    // Category 8: LayoutKind.Auto Determinism Enforcement
    // ══════════════════════════════════════════════════════—

    /// <summary>
    /// Components with [StructLayout(LayoutKind.Auto)] must be rejected at
    /// storage time. LayoutKind.Auto lets the CLR reorder fields for optimal
    /// alignment. Two hosts running different JIT versions or different CPU
    /// architectures may reorder differently, producing different byte layouts
    /// for the same struct. Since Archetype stores components as raw bytes and
    /// CanonicalChecksum hashes those bytes directly, cross-host lockstep would
    /// silently diverge.
    ///
    /// This test verifies the fix: LayoutKind.Auto components throw
    /// NotSupportedException when added to the world.
    /// </summary>
    [Fact]
    public void Layoutkind_auto_component_rejected_for_determinism()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        // AutoLayoutByte has LayoutKind.Auto, no Entity fields, no managed refs.
        // The fix rejects it at storage time to enforce cross-host determinism.
        Assert.Throws<NotSupportedException>(() => world.Add(entity, new AutoLayoutByte(1, 2, 3)));
    }

    /// <summary>
    /// Verify that LayoutKind.Sequential components are still accepted.
    /// Only LayoutKind.Auto is rejected; Sequential and Explicit are fine.
    /// </summary>
    [Fact]
    public void Layoutkind_sequential_component_accepted_normally()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        // SequentialLayoutMixed has LayoutKind.Sequential — must work fine.
        world.Add(entity, new SequentialLayoutMixed(1, 2, 3));
        Assert.True(world.TryGet(entity, out SequentialLayoutMixed value));
        Assert.Equal(1, value.A);
        Assert.Equal(2, value.B);
        Assert.Equal(3, value.C);
    }

    /// <summary>
    /// Verify that record struct components (which default to Sequential)
    /// are still accepted. This covers the common case.
    /// </summary>
    [Fact]
    public void Record_struct_component_accepted_normally()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        // Position is a record struct — defaults to LayoutKind.Sequential.
        world.Add(entity, new Position(42, 99));
        Assert.True(world.TryGet(entity, out Position p));
        Assert.Equal(new Position(42, 99), p);
    }

    /// <summary>
    /// Verify that enum components are accepted. Enums have a fixed primitive
    /// underlying layout and cannot reorder fields, even though reflection reports
    /// LayoutKind.Auto for enum types.
    /// </summary>
    [Fact]
    public void Enum_component_accepted_normally()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        world.Add(entity, TestEnum.Beta);
        Assert.True(world.TryGet(entity, out TestEnum value));
        Assert.Equal(TestEnum.Beta, value);
    }

    // ── Test types for LayoutKind.Auto vulnerability ──

    private enum TestEnum
    {
        Alpha = 0,
        Beta = 1
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private struct AutoLayoutByte
    {
        public byte A;
        public byte B;
        public byte C;
        public AutoLayoutByte(byte a, byte b, byte c) { A = a; B = b; C = c; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private struct AutoLayoutMixed
    {
        public byte A;
        public int B;
        public short C;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SequentialLayoutMixed
    {
        public byte A;
        public int B;
        public short C;
        public SequentialLayoutMixed(byte a, int b, short c) { A = a; B = b; C = c; }
    }
}


