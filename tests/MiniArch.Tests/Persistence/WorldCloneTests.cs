using MiniArch.Core;

namespace MiniArchTests.Persistence;

public sealed class WorldCloneTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Clone_preserves_entity_metadata_values_and_archetype_membership()
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

        var cloned = world.Clone();

        Assert.True(cloned.TryGetLocation(first, out var firstLocation));
        Assert.Equal(first.Version, firstLocation.Version);
        Assert.Equal(1, firstLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(1, 2), GetComponent<Position>(cloned, first));

        Assert.True(cloned.TryGetLocation(second, out var secondLocation));
        Assert.Equal(second.Version, secondLocation.Version);
        Assert.Equal(2, secondLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(3, 4), GetComponent<Position>(cloned, second));
        Assert.Equal(new Velocity(5, 6), GetComponent<Velocity>(cloned, second));

        Assert.True(cloned.TryGetLocation(third, out var thirdLocation));
        Assert.Equal(third.Version, thirdLocation.Version);
        Assert.Equal(2, thirdLocation.Archetype.Signature.Count);
        Assert.Equal(new Position(11, 12), GetComponent<Position>(cloned, third));
        Assert.Equal(new Velocity(9, 10), GetComponent<Velocity>(cloned, third));
    }

    [Fact]
    public void Clone_preserves_multiple_archetypes_and_multiple_chunks()
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

        var cloned = world.Clone();

        foreach (var entity in positionOnly)
        {
            Assert.True(cloned.TryGetLocation(entity, out var location));
            Assert.Equal(1, location.Archetype.Signature.Count);
            Assert.Equal(new Position(entity.Id, entity.Id + 10), GetComponent<Position>(cloned, entity));
        }

        for (var i = 0; i < moving.Length; i++)
        {
            var entity = moving[i];
            Assert.True(cloned.TryGetLocation(entity, out var location));
            Assert.Equal(2, location.Archetype.Signature.Count);
            Assert.Equal(new Position(i + 100, i + 110), GetComponent<Position>(cloned, entity));
            Assert.Equal(new Velocity(i + 120, i + 130), GetComponent<Velocity>(cloned, entity));
        }

        for (var i = 0; i < living.Length; i++)
        {
            var entity = living[i];
            Assert.True(cloned.TryGetLocation(entity, out var location));
            Assert.Equal(1, location.Archetype.Signature.Count);
            Assert.Equal(new Health(i + 200), GetComponent<Health>(cloned, entity));
        }

        Assert.True(cloned.TryGetLocation(positionOnly[2], out var thirdPositionOnlyLocation));
        Assert.NotNull(thirdPositionOnlyLocation.Archetype);

        Assert.True(cloned.TryGetLocation(moving[2], out var thirdMovingLocation));
        Assert.NotNull(thirdMovingLocation.Archetype);

        Assert.True(cloned.TryGetLocation(living[2], out var thirdLivingLocation));
        Assert.NotNull(thirdLivingLocation.Archetype);
    }

    [Fact]
    public void Clone_preserves_free_slot_versions_for_reused_entity_ids()
    {
        var world = new World();
        var original = world.Create();

        world.Destroy(original);

        var cloned = world.Clone();
        var recreated = cloned.Create();

        Assert.Equal(original.Id, recreated.Id);
        Assert.Equal(original.Version + 1, recreated.Version);
        Assert.False(cloned.TryGetLocation(original, out _));
        Assert.True(cloned.TryGetLocation(recreated, out _));
    }

    [Fact]
    public void Clone_preserves_parent_and_children_relationships()
    {
        var world = new World();
        var parent = world.Create();
        var firstChild = world.Create();
        var secondChild = world.Create();

        world.Link(parent, firstChild);
        world.Link(parent, secondChild);

        var cloned = world.Clone();

        Assert.True(cloned.TryGetParent(firstChild, out var resolvedParent));
        Assert.Equal(parent, resolvedParent);
        Assert.Equal(
            [firstChild, secondChild],
            cloned.GetChildren(parent).OrderBy(entity => entity.Id).ToArray());
    }

    [Fact]
    public void Clone_restores_hierarchy_so_cascade_destroy_still_works()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();

        world.Link(root, child);
        world.Link(child, grandChild);

        var cloned = world.Clone();
        cloned.Destroy(root);

        Assert.False(cloned.IsAlive(root));
        Assert.False(cloned.IsAlive(child));
        Assert.False(cloned.IsAlive(grandChild));
    }

    [Fact]
    public void Clone_produces_independent_copy()
    {
        var world = new World(chunkCapacity: 4);
        var entity = world.Create();
        world.Add(entity, new Position(1, 2));

        var cloned = world.Clone();

        world.Set(entity, new Position(99, 99));

        Assert.Equal(new Position(1, 2), GetComponent<Position>(cloned, entity));
        Assert.Equal(new Position(99, 99), GetComponent<Position>(world, entity));
    }

    [Fact]
    public void Clone_empty_world()
    {
        var world = new World();
        var cloned = world.Clone();

        Assert.NotNull(cloned);
    }

    private static T GetComponent<T>(World world, Entity entity)
    {
        Assert.True(world.TryGetLocation(entity, out var location));

        var componentType = ComponentRegistry.Shared.GetOrCreate<T>();
        return location.Archetype.GetComponent<T>(componentType, location.RowIndex);
    }
}

