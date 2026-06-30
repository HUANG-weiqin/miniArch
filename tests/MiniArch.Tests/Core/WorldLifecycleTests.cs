using System.Runtime.ExceptionServices;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;
using MiniQuery = MiniArch.Core.Query;

namespace MiniArchTests.Core;

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
        Assert.Equal(1, entity.Version);
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
        var positionId = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityId = ComponentRegistry.Shared.GetOrCreate<Velocity>();
        Assert.Contains(positionId, info.Archetype.Signature);
        Assert.Contains(velocityId, info.Archetype.Signature);
        Assert.Equal(0, info.RowIndex);
    }

    [Fact]
    public void Create_with_components_places_entity_directly_into_final_archetype_without_intermediate_archetypes()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2), new Velocity(3, 4));
        var positionId = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityId = ComponentRegistry.Shared.GetOrCreate<Velocity>();

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(2, info.Archetype.Signature.Count);
        Assert.Contains(positionId, info.Archetype.Signature);
        Assert.Contains(velocityId, info.Archetype.Signature);

        Assert.Equal(new Position(1, 2), info.Archetype.GetComponentAt<Position>(info.Archetype.GetComponentIndex(positionId), info.RowIndex));
        Assert.Equal(new Velocity(3, 4), info.Archetype.GetComponentAt<Velocity>(info.Archetype.GetComponentIndex(velocityId), info.RowIndex));

        var positionQuery = CreateQuery<Position>(world);
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
        var c1 = ComponentRegistry.Shared.GetOrCreate<C1>();
        var c16 = ComponentRegistry.Shared.GetOrCreate<C16>();

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(16, info.Archetype.Signature.Count);

        Assert.Equal(new C1(1), info.Archetype.GetComponentAt<C1>(info.Archetype.GetComponentIndex(c1), info.RowIndex));
        Assert.Equal(new C16(16), info.Archetype.GetComponentAt<C16>(info.Archetype.GetComponentIndex(c16), info.RowIndex));

        var matchedArchetypes = CreateQuery<C1>(world).MatchedArchetypes;
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
            Assert.Equal(i, info.RowIndex);
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
            Assert.Equal(i, info.RowIndex);
        }

        for (var i = 0; i < secondBatch.Length; i++)
        {
            Assert.True(world.TryGetLocation(secondBatch[i], out var info));
            var absoluteIndex = firstBatch.Length + i;
            Assert.Equal(absoluteIndex, info.RowIndex);
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
            Assert.Equal(2, entity.Version);
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
            Assert.Equal(2, entity.Version);
            Assert.True(world.TryGetLocation(entity, out _));
        }

        foreach (var entity in secondBatch.Where(entity => entity.Id >= 6))
        {
            Assert.Equal(1, entity.Version);
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
        Assert.Equal(4, firstReused.RowIndex);

        Assert.True(world.TryGetLocation(secondBatch[1], out var secondReused));
        Assert.Equal(5, secondReused.RowIndex);

        Assert.True(world.TryGetLocation(secondBatch[2], out var firstFresh));
        Assert.Equal(6, firstFresh.RowIndex);

        Assert.True(world.TryGetLocation(secondBatch[3], out var secondFresh));
        Assert.Equal(7, secondFresh.RowIndex);
    }

    // Regression: CreateMany used to call archetype.GetReservedEntities, which
    // throws when the archetype has been promoted to chunked storage. A batch
    // create on an already-chunked archetype must not crash.
    [Fact]
    public void CreateMany_works_when_archetype_is_already_chunked()
    {
        var world = new World();
        // Force the lazy empty archetype into existence, then promote it to
        // chunked storage so we exercise the per-row write path.
        var empty = world.GetOrCreateArchetype(Signature.Empty);
        empty.ForceChunkedForTesting();
        Assert.True(empty.IsChunked);

        var batch = new Entity[5];
        world.CreateMany(batch);

        Assert.Equal(empty.EntityCount, batch.Length);
        for (var i = 0; i < batch.Length; i++)
        {
            Assert.True(batch[i].IsValid);
            Assert.True(world.TryGetLocation(batch[i], out var info));
            Assert.Equal(empty, info.Archetype);
            Assert.Equal(i, info.RowIndex);
        }
    }

    // Regression: CreateMany with mixed recycled+fresh ids on a chunked
    // archetype exercises both inner loops of the chunked write path.
    [Fact]
    public void CreateMany_on_chunked_archetype_mixes_recycled_and_fresh_ids()
    {
        var world = new World();
        var seed = new Entity[4];
        world.CreateMany(seed);
        foreach (var e in seed) world.Destroy(e);

        var empty = world.GetOrCreateArchetype(Signature.Empty);
        empty.ForceChunkedForTesting();
        Assert.True(empty.IsChunked);

        var batch = new Entity[6];
        world.CreateMany(batch);

        // First 4 ids come from the free list (recycled), last 2 are fresh.
        Assert.Equal(empty.EntityCount, batch.Length);
        for (var i = 0; i < batch.Length; i++)
        {
            Assert.True(world.TryGetLocation(batch[i], out var info));
            Assert.Equal(i, info.RowIndex);
        }
    }

    [Fact]
    public void Default_world_scales_chunk_capacity_for_dense_component_archetypes()
    {
        var world = new World();

        for (var i = 0; i < 256; i++)
        {
            world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
        }

        var query = CreateQuery<Position, Velocity>(world);

        Assert.Single(query.MatchedArchetypes);
        Assert.Equal(1, query.GetArchetypeSpan().Length);
    }

    [Fact]
    public void Explicit_chunk_capacity_keeps_fixed_chunk_boundaries()
    {
        var world = new World(chunkCapacity: 4);

        for (var i = 0; i < 5; i++)
        {
            world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
        }

        var query = CreateQuery<Position, Velocity>(world);

        Assert.Single(query.MatchedArchetypes);
        Assert.Equal(1, query.GetArchetypeSpan().Length);
    }

    [Fact]
    public void Link_stores_parent_relationship_and_children_list()
    {
        var world = new World();
        var parent = world.Create();
        var firstChild = world.Create();
        var secondChild = world.Create();

        world.AddChild(parent, firstChild);
        world.AddChild(parent, secondChild);

        Assert.True(world.TryGetParent(firstChild, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);

        var children = world.EnumerateChildren(parent).ToChildList();
        Assert.Equal(2, children.Count);
        Assert.Equal([firstChild, secondChild], children.OrderBy(entity => entity.Id).ToArray());
    }

    private static MiniQuery CreateQuery<T>(World world)
    {
        var description = new QueryDescription().With<T>();
        return MiniQuery.Create(world, in description);
    }

    private static MiniQuery CreateQuery<T1, T2>(World world)
    {
        var description = new QueryDescription().With<T1>().With<T2>();
        return MiniQuery.Create(world, in description);
    }

    [Fact]
    public void Link_reparents_child_to_latest_parent()
    {
        var world = new World();
        var firstParent = world.Create();
        var secondParent = world.Create();
        var child = world.Create();

        world.AddChild(firstParent, child);
        world.AddChild(secondParent, child);

        Assert.True(world.TryGetParent(child, out var resolvedParent));
        Assert.Equal(secondParent, resolvedParent);
        Assert.False(world.HasChildren(firstParent));
        Assert.Equal([child], world.EnumerateChildren(secondParent).ToChildList());
    }

    [Fact]
    public void Link_rejects_self_link()
    {
        var world = new World();
        var e = world.Create();

        Assert.Throws<InvalidOperationException>(() => world.AddChild(e, e));
    }

    [Fact]
    public void Link_rejects_direct_cycle()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();

        world.AddChild(parent, child);
        // child -> parent would close the cycle
        Assert.Throws<InvalidOperationException>(() => world.AddChild(child, parent));
    }

    [Fact]
    public void Link_rejects_indirect_cycle()
    {
        var world = new World();
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();

        world.AddChild(a, b);
        world.AddChild(b, c);
        // c -> a closes a three-deep cycle
        Assert.Throws<InvalidOperationException>(() => world.AddChild(c, a));
    }

    // Regression: snapshot/clone restore path bypassed ValidateAddChild, which
    // meant a tampered snapshot could install a hierarchy cycle that later
    // hung CollectDestroySubtree. AddChildFromSnapshot must reject cycles too.
    [Fact]
    public void AddChildFromSnapshot_rejects_cycle_in_restored_hierarchy()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();

        // Establish a legitimate AddChild so a cycle is now possible.
        world.AddChildFromSnapshot(parent, child);

        // Attempt to close the cycle through the restore path.
        Assert.Throws<InvalidOperationException>(() => world.AddChildFromSnapshot(child, parent));
    }

    [Fact]
    public void AddChildFromSnapshot_rejects_self_link()
    {
        var world = new World();
        var e = world.Create();

        Assert.Throws<InvalidOperationException>(() => world.AddChildFromSnapshot(e, e));
    }

    [Fact]
    public void Destroy_parent_cascades_to_all_descendants()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();

        world.AddChild(root, child);
        world.AddChild(child, grandChild);

        world.Destroy(root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandChild));
        Assert.False(world.HasChildren(root));
    }

    [Fact]
    public void Warmed_destroy_cascade_path_does_not_allocate()
    {
        RunOnDedicatedThread(() =>
        {
            WarmupDestroyCascadeAllocations();

            var world = new World();
            var root = world.Create();
            var child = world.Create();
            var grandChild = world.Create();

            world.AddChild(root, child);
            world.AddChild(child, grandChild);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            world.Destroy(root);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.False(world.IsAlive(root));
            Assert.False(world.IsAlive(child));
            Assert.False(world.IsAlive(grandChild));
            Assert.Equal(0, allocated);
        });
    }

    [Fact]
    public void Warmed_create_with_components_does_not_allocate_for_archetype_lookup()
    {
        RunOnDedicatedThread(() =>
        {
            const int EntityCount = 256;
            var world = new World(chunkCapacity: EntityCount + 1, entityCapacity: EntityCount + 1);
            world.EnsureCapacity(EntityCount + 1);
            world.Create(new Position(-1, -1), new Velocity(-2, -2));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < EntityCount; i++)
            {
                world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1024, $"Expected no per-entity archetype lookup allocations, but allocated {allocated} bytes.");
        });
    }

    [Fact]
    public void Warmed_empty_replay_does_not_allocate()
    {
        RunOnDedicatedThread(() =>
        {
            var world = new World();
            var delta = new FrameDelta();
            world.Replay(delta);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            world.Replay(delta);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        });
    }

    [Fact]
    public void Component_type_is_resolved_via_global_static_cache()
    {
        var ct = Component<Position>.ComponentType;
        Assert.True(ct.IsValid);

        var world = new World();
        var entity = world.Create(new Position(1, 2));
        Assert.True(world.IsAlive(entity));
    }

    [Fact]
    public void Reused_entity_slot_does_not_inherit_destroyed_relationship()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();

        world.AddChild(parent, child);
        world.Destroy(child);

        var replacement = world.Create();

        Assert.Equal(child.Id, replacement.Id);
        Assert.NotEqual(child.Version, replacement.Version);
        Assert.False(world.TryGetParent(replacement, out _));
        Assert.False(world.HasChildren(parent));
    }

    private static void WarmupDestroyCascadeAllocations()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();

        world.AddChild(root, child);
        world.AddChild(child, grandChild);
        world.Destroy(root);
    }

    [Fact]
    public void GetFirst_returns_first_entity_with_component()
    {
        var world = new World();
        var created = world.Create(new Position(1, 2));

        var first = world.GetFirst<Position>();

        Assert.Equal(created, first);
        Assert.Equal(1, world.Get<Position>(first).X);
    }

    [Fact]
    public void GetFirst_throws_when_no_entity_with_component()
    {
        var world = new World();

        Assert.Throws<InvalidOperationException>(() => world.GetFirst<Position>());
    }

    [Fact]
    public void GetFirst_throws_when_entity_destroyed()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        world.Destroy(entity);

        Assert.Throws<InvalidOperationException>(() => world.GetFirst<Position>());
    }

    private static void RunOnDedicatedThread(Action action)
    {
        Exception? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }
        });

        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            ExceptionDispatchInfo.Capture(capturedException).Throw();
        }
    }

    [Fact]
    public void TryReleaseReserved_releases_reserved_entity_and_returns_true()
    {
        var world = new World();
        var reserved = world.ReserveDeferredEntity();

        var released = world.TryReleaseReserved(reserved);

        Assert.True(released);
        // The id must be back in the free list: the next reserve reuses it.
        var next = world.ReserveDeferredEntity();
        Assert.Equal(reserved.Id, next.Id);
        Assert.NotEqual(reserved.Version, next.Version);
    }
    [Fact]
    public void TryReleaseReserved_returns_false_for_alive_entity()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        var released = world.TryReleaseReserved(entity);

        Assert.False(released);
        Assert.True(world.IsAlive(entity));
    }

    [Fact]
    public void TryReleaseReserved_returns_false_for_already_released()
    {
        var world = new World();
        var reserved = world.ReserveDeferredEntity();

        var first = world.TryReleaseReserved(reserved);
        var second = world.TryReleaseReserved(reserved);

        Assert.True(first);
        Assert.False(second); // version bumped after first release, no longer matches
    }

    [Fact]
    public void TryReleaseReserved_returns_false_for_destroyed_entity()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        world.Destroy(entity);

        var released = world.TryReleaseReserved(entity);

        Assert.False(released);
    }
}

