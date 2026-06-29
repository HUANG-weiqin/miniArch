using System.IO;
using MiniArch.Core;

namespace MiniArchTests.Persistence;

public sealed class WorldSnapshotTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Unmanaged_world_can_round_trip_preserving_entity_metadata_values_and_archetype_membership()
    {
        var world = new World(chunkCapacity: 2);

        var first = world.Create();
        var second = world.Create();
        var third = world.Create();

        world.Add(first, new Position(1, 2));
        world.Add(second, new Position(3, 4));
        world.Add(second, new Velocity(5, 6));
        world.Add(third, new Position(7, 8));
        world.Add(third, new Velocity(9, 10));
        world.Set(third, new Position(11, 12));

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        Assert.True(loaded.TryGetLocation(first, out var firstLocation));
        Assert.Equal(first.Version, firstLocation.Version);
        Assert.Equal(1, firstLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(1, 2), GetComponent<Position>(loaded, first));

        Assert.True(loaded.TryGetLocation(second, out var secondLocation));
        Assert.Equal(second.Version, secondLocation.Version);
        Assert.Equal(2, secondLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(3, 4), GetComponent<Position>(loaded, second));
        Assert.Equal(new Velocity(5, 6), GetComponent<Velocity>(loaded, second));

        Assert.True(loaded.TryGetLocation(third, out var thirdLocation));
        Assert.Equal(third.Version, thirdLocation.Version);
        Assert.Equal(2, thirdLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(11, 12), GetComponent<Position>(loaded, third));
        Assert.Equal(new Velocity(9, 10), GetComponent<Velocity>(loaded, third));
    }

    [Fact]
    public void Snapshot_round_trip_preserves_multiple_archetypes_and_multiple_chunks()
    {
        var world = new World(chunkCapacity: 2);

        var positionOnly = new Entity[3];
        var moving = new Entity[3];
        var living = new Entity[3];

        for (var i = 0; i < positionOnly.Length; i++)
        {
            positionOnly[i] = world.Create();
            world.Add(positionOnly[i], new Position(i, i + 10));
        }

        for (var i = 0; i < moving.Length; i++)
        {
            moving[i] = world.Create();
            world.Add(moving[i], new Position(i + 100, i + 110));
            world.Add(moving[i], new Velocity(i + 120, i + 130));
        }

        for (var i = 0; i < living.Length; i++)
        {
            living[i] = world.Create();
            world.Add(living[i], new Health(i + 200));
        }

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        foreach (var entity in positionOnly)
        {
            Assert.True(loaded.TryGetLocation(entity, out var location));
            Assert.Equal(1, location.Archetype.Signature.Count);
            Assert.Equal(new Position(entity.Id, entity.Id + 10), GetComponent<Position>(loaded, entity));
        }

        for (var i = 0; i < moving.Length; i++)
        {
            var entity = moving[i];
            Assert.True(loaded.TryGetLocation(entity, out var location));
            Assert.Equal(2, location.Archetype.Signature.Count);
            Assert.Equal(new Position(i + 100, i + 110), GetComponent<Position>(loaded, entity));
            Assert.Equal(new Velocity(i + 120, i + 130), GetComponent<Velocity>(loaded, entity));
        }

        for (var i = 0; i < living.Length; i++)
        {
            var entity = living[i];
            Assert.True(loaded.TryGetLocation(entity, out var location));
            Assert.Equal(1, location.Archetype.Signature.Count);
            Assert.Equal(new Health(i + 200), GetComponent<Health>(loaded, entity));
        }

        Assert.True(loaded.TryGetLocation(positionOnly[2], out var thirdPositionOnlyLocation));
        Assert.NotNull(thirdPositionOnlyLocation.Archetype);

        Assert.True(loaded.TryGetLocation(moving[2], out var thirdMovingLocation));
        Assert.NotNull(thirdMovingLocation.Archetype);

        Assert.True(loaded.TryGetLocation(living[2], out var thirdLivingLocation));
        Assert.NotNull(thirdLivingLocation.Archetype);
    }


    [Fact]
    public void Snapshot_preserves_free_slot_versions_for_reused_entity_ids()
    {
        var world = new World();
        var original = world.Create();

        world.Destroy(original);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        var recreated = loaded.Create();

        Assert.Equal(original.Id, recreated.Id);
        Assert.Equal(original.Version + 1, recreated.Version);
        Assert.False(loaded.TryGetLocation(original, out _));
        Assert.True(loaded.TryGetLocation(recreated, out _));
    }

    [Fact]
    public void Snapshot_preserves_parent_and_children_relationships()
    {
        var world = new World();
        var parent = world.Create();
        var firstChild = world.Create();
        var secondChild = world.Create();

        world.Link(parent, firstChild);
        world.Link(parent, secondChild);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);

        Assert.True(loaded.TryGetParent(firstChild, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);
        Assert.Equal(
            [firstChild, secondChild],
            loaded.GetChildren(parent).OrderBy(entity => entity.Id).ToArray());
    }

    [Fact]
    public void Snapshot_restores_hierarchy_so_cascade_destroy_still_works()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();

        world.Link(root, child);
        world.Link(child, grandChild);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;

        var loaded = WorldSnapshot.Load(stream);
        loaded.Destroy(root);

        Assert.False(loaded.IsAlive(root));
        Assert.False(loaded.IsAlive(child));
        Assert.False(loaded.IsAlive(grandChild));
    }

    [Fact]
    public void Save_canonicalizes_entity_row_order_within_archetype_so_load_yields_id_ascending_layout()
    {
        // Build a world where archetype internal rows are NOT in id order
        // (swap-remove on row 0 moves the last entity into the first slot).
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(10, 20));
        var e1 = world.Create(); world.Add(e1, new Position(30, 40));
        var e2 = world.Create(); world.Add(e2, new Position(50, 60));
        world.Destroy(e0); // swap-remove: e2 moves to row 0 -> internal [e2, e1]

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        // After canonical Save+Load, archetype rows should follow ascending entity id.
        Assert.True(loaded.TryGetLocation(e1, out var e1Location));
        Assert.True(loaded.TryGetLocation(e2, out var e2Location));
        Assert.Equal(0, e1Location.RowIndex);
        Assert.Equal(1, e2Location.RowIndex);
        Assert.Equal(new Position(30, 40), GetComponent<Position>(loaded, e1));
        Assert.Equal(new Position(50, 60), GetComponent<Position>(loaded, e2));
    }

    [Fact]
    public void Save_is_idempotent_after_round_trip_with_non_canonical_internal_layout()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(10, 20));
        var e1 = world.Create(); world.Add(e1, new Position(30, 40));
        var e2 = world.Create(); world.Add(e2, new Position(50, 60));
        world.Destroy(e0); // internal archetype rows: [e2, e1]

        using var stream1 = new MemoryStream();
        WorldSnapshot.Save(stream1, world);

        stream1.Position = 0;
        var loaded = WorldSnapshot.Load(stream1);

        using var stream2 = new MemoryStream();
        WorldSnapshot.Save(stream2, loaded);

        Assert.Equal(stream1.ToArray(), stream2.ToArray());
    }

    [Fact]
    public void Checksum_is_stable_for_identical_worlds_and_differs_on_mutation()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(1, 2));
        var e1 = world.Create(); world.Add(e1, new Position(3, 4));

        var hash1 = world.Checksum();
        var hash2 = world.Checksum();

        Assert.Equal(32, hash1.Length);
        Assert.Equal(hash1, hash2);

        world.Set(e0, new Position(99, 99));
        var hash3 = world.Checksum();
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void Checksum_matches_across_save_load_round_trip()
    {
        var world = new World();
        var e0 = world.Create(); world.Add(e0, new Position(1, 2));
        var e1 = world.Create(); world.Add(e1, new Velocity(3, 4));

        var hashOriginal = world.Checksum();

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        var hashLoaded = loaded.Checksum();
        Assert.Equal(hashOriginal, hashLoaded);
    }

    [Fact]
    public void Save_load_preserves_free_id_allocation_order()
    {
        var world = new World();
        var e0 = world.Create(new Position(0, 0));
        var e1 = world.Create(new Position(1, 1));
        var e2 = world.Create(new Position(2, 2));
        var e3 = world.Create(new Position(3, 3));
        var e4 = world.Create(new Position(4, 4));

        // Destroy in non-descending order to create a specific LIFO free list.
        // Free list after these destroys (push order): [1, 3, 4]
        // Pop order on next Create: 4, 3, 1
        world.Destroy(e1);
        world.Destroy(e3);
        world.Destroy(e4);

        using var stream = new MemoryStream();
        WorldSnapshot.Save(stream, world);
        stream.Position = 0;
        var loaded = WorldSnapshot.Load(stream);

        // Both worlds should allocate the same recycled ids in the same order.
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();

        var la = loaded.Create();
        var lb = loaded.Create();
        var lc = loaded.Create();

        Assert.Equal(a.Id, la.Id);
        Assert.Equal(b.Id, lb.Id);
        Assert.Equal(c.Id, lc.Id);
    }

    private static T GetComponent<T>(World world, Entity entity) where T : unmanaged
    {
        Assert.True(world.TryGetLocation(entity, out var location));

        var componentType = ComponentRegistry.Shared.GetOrCreate<T>();
        return location.Archetype.GetComponentAt<T>(location.Archetype.GetComponentIndex(componentType), location.RowIndex);
    }
}

