using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

namespace MiniArchTests.Core;

public sealed class WorldStructuralChangeTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Add_moves_entity_to_destination_archetype()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Single(info.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Position>(), info.Archetype.Signature);
    }

    [Fact]
    public void Set_updates_existing_component_in_place()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        Assert.True(world.TryGetLocation(entity, out var before));

        world.Set(entity, new Position(9, 9));

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.Same(before.Archetype, after.Archetype);
        Assert.Equal(before.ChunkIndex, after.ChunkIndex);
        Assert.Equal(before.RowIndex, after.RowIndex);
        Assert.Equal(new Position(9, 9), after.Archetype.GetChunk(after.ChunkIndex).GetComponent<Position>(world.Components.GetOrCreate<Position>(), after.RowIndex));
    }

    [Fact]
    public void Set_only_mutates_the_target_component_when_multiple_components_are_present()
    {
        var world = new World();
        var entity = world.Create();

        var positionId = world.Components.GetOrCreate<Position>();
        var velocityId = world.Components.GetOrCreate<Velocity>();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));
        Assert.True(world.TryGetLocation(entity, out var before));

        world.Set(entity, new Position(9, 9));

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.Same(before.Archetype, after.Archetype);
        Assert.Equal(before.ChunkIndex, after.ChunkIndex);
        Assert.Equal(before.RowIndex, after.RowIndex);

        var chunk = after.Archetype.GetChunk(after.ChunkIndex);
        Assert.Equal(new Position(9, 9), chunk.GetComponent<Position>(positionId, after.RowIndex));
        Assert.Equal(new Velocity(3, 4), chunk.GetComponent<Velocity>(velocityId, after.RowIndex));
    }

    [Fact]
    public void Remove_moves_entity_back_to_smaller_archetype()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));

        Assert.True(world.TryGetLocation(entity, out var before));

        world.Remove<Velocity>(entity);

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.NotSame(before.Archetype, after.Archetype);
        Assert.Single(after.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Position>(), after.Archetype.Signature);
        Assert.DoesNotContain(world.Components.GetOrCreate<Velocity>(), after.Archetype.Signature);
    }

    [Fact]
    public void Bulk_set_over_many_entities_keeps_locations_valid()
    {
        var world = new World();
        var entities = new List<Entity>();
        var positionId = world.Components.GetOrCreate<Position>();

        for (var i = 0; i < 1000; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
            entities.Add(entity);

            Assert.True(world.TryGetLocation(entities[0], out var firstLocation));
            Assert.Contains(positionId, firstLocation.Archetype.Signature);
        }

        foreach (var entity in entities)
        {
            Assert.True(world.TryGetLocation(entity, out var before));
            Assert.Contains(positionId, before.Archetype.Signature);
            try
            {
                world.Set(entity, new Position(42, 42));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Set failed for entity {entity}.", ex);
            }
            Assert.True(world.TryGetLocation(entity, out var after));
            Assert.Same(before.Archetype, after.Archetype);
            Assert.Equal(before.ChunkIndex, after.ChunkIndex);
            Assert.Equal(before.RowIndex, after.RowIndex);
        }
    }

    [Fact]
    public void Replay_with_reverse_restores_destroyed_entity_components_and_query_visibility()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));
        world.Add(entity, new Health(5));

        var buffer = new CommandBuffer(world);
        buffer.Destroy(entity);
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.False(world.IsAlive(entity));
        Assert.Equal(0, CountQueryEntities(CreateQuery<Position>(world)));

        world.Rewind(in reverse);

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Contains(world.Components.GetOrCreate<Position>(), info.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Velocity>(), info.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Health>(), info.Archetype.Signature);
        Assert.Equal(1, CountQueryEntities(CreateQuery<Position>(world)));
        Assert.Equal(new Position(1, 2), info.Archetype.GetChunk(info.ChunkIndex).GetComponent<Position>(world.Components.GetOrCreate<Position>(), info.RowIndex));
    }

    [Fact]
    public void Add_existing_component_overwrites_and_rewind_restores_previous_value()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position(1, 2));

        var buffer = new CommandBuffer(world);
        buffer.Add(entity, new Position(9, 9));
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.Equal(new Position(9, 9), GetComponentValue(world, entity));

        world.Rewind(in reverse);

        Assert.Equal(new Position(1, 2), GetComponentValue(world, entity));
    }

    [Fact]
    public void Set_missing_component_adds_component_and_rewind_removes_it()
    {
        var world = new World();
        var entity = world.Create();

        var buffer = new CommandBuffer(world);
        buffer.Set(entity, new Position(9, 9));
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);

        Assert.True(HasComponent<Position>(world, entity));
        Assert.Equal(new Position(9, 9), GetComponentValue(world, entity));

        world.Rewind(in reverse);

        Assert.False(HasComponent<Position>(world, entity));
    }

    [Fact]
    public void Remove_missing_component_is_noop_and_rewind_keeps_world_unchanged()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Velocity(3, 4));
        var before = CaptureEntityState(world, entity);

        var buffer = new CommandBuffer(world);
        buffer.Remove<Position>(entity);
        var frame = buffer.Playback();

        var reverse = world.ReplayWithReverse(in frame);
        var afterReplay = CaptureEntityState(world, entity);

        Assert.Equal(before, afterReplay);

        world.Rewind(in reverse);

        Assert.Equal(before, CaptureEntityState(world, entity));
    }

    private static Position GetComponentValue(World world, Entity entity)
    {
        Assert.True(world.TryGetLocation(entity, out var info));
        return info.Archetype.GetChunk(info.ChunkIndex).GetComponent<Position>(world.Components.GetOrCreate<Position>(), info.RowIndex);
    }

    private static bool HasComponent<T>(World world, Entity entity)
    {
        return world.TryGetLocation(entity, out var info) && info.Archetype.Signature.Contains(world.Components.GetOrCreate<T>());
    }

    private static string CaptureEntityState(World world, Entity entity)
    {
        if (!world.TryGetLocation(entity, out var info))
        {
            return $"{entity}:dead";
        }

        var parts = new List<string>
        {
            $"signature={info.Archetype.Signature}",
            $"position={(HasComponent<Position>(world, entity) ? GetComponentValue(world, entity).ToString() : "none")}",
            $"velocity={(HasComponent<Velocity>(world, entity) ? info.Archetype.GetChunk(info.ChunkIndex).GetComponent<Velocity>(world.Components.GetOrCreate<Velocity>(), info.RowIndex).ToString() : "none")}",
            $"health={(HasComponent<Health>(world, entity) ? info.Archetype.GetChunk(info.ChunkIndex).GetComponent<Health>(world.Components.GetOrCreate<Health>(), info.RowIndex).ToString() : "none")}",
            $"query:Position={CountQueryEntities(CreateQuery<Position>(world))}",
            $"query:Velocity={CountQueryEntities(CreateQuery<Velocity>(world))}",
            $"query:Health={CountQueryEntities(CreateQuery<Health>(world))}"
        };

        return string.Join("|", parts);
    }

    private static int CountQueryEntities(MiniArch.Core.Query query)
    {
        var total = 0;
        foreach (ref readonly var chunk in query.GetChunkSpan())
        {
            total += chunk.GetEntities().Length;
        }

        return total;
    }

    private static MiniQuery CreateQuery<T>(World world)
    {
        var description = new QueryDescription().With<T>();
        return MiniQuery.Create(world, in description);
    }
}
