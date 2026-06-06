using System.Linq;
using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

namespace MiniArchTests.Core;

public sealed class IntegrationTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Entity_can_move_between_archetypes_and_be_queried_with_final_values()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));
        world.Set(entity, new Position(9, 9));
        world.Remove<Velocity>(entity);

        var positionId = world.Components.GetOrCreate<Position>();
        var description = new QueryDescription().With<Position>();
        var query = MiniQuery.Create(world, in description);

        Assert.True(world.TryGetLocation(entity, out var location));
        Assert.Equal(1, location.Archetype.Signature.Count);
        Assert.Contains(positionId, location.Archetype.Signature);

        var chunks = query.Chunks.ToList();
        Assert.Equal(2, query.MatchedArchetypes.Count);
        Assert.Single(chunks);
        Assert.Contains(chunks, chunk => chunk.Count == 1);

        Assert.Equal(entity, location.Archetype.GetEntity(location.RowIndex));
        Assert.Equal(new Position(9, 9), location.Archetype.GetComponent<Position>(positionId, location.RowIndex));
    }
}
