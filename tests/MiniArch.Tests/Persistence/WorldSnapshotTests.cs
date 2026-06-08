using System.IO;
using MiniArch.Core;

namespace MiniArchTests.Persistence;

public sealed class WorldSnapshotTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);
    private readonly record struct Name(string Value);

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
    public void Adding_a_component_with_managed_references_fails_with_a_clear_exception()
    {
        var world = new World();
        var entity = world.Create();

        var ex = Assert.Throws<NotSupportedException>(() => world.Add(entity, new Name("npc-01")));

        Assert.Contains(nameof(Name), ex.Message);
        Assert.Contains("managed references", ex.Message);
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

    private static T GetComponent<T>(World world, Entity entity)
    {
        Assert.True(world.TryGetLocation(entity, out var location));

        var componentType = ComponentRegistry.Shared.GetOrCreate<T>();
        return location.Archetype.GetComponentAt<T>(location.Archetype.GetComponentIndex(componentType), location.RowIndex);
    }
}

