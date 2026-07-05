using MiniArch.Diagnostics;

namespace MiniArchTests.Diagnostics;

file readonly record struct Position(int X, int Y);
file readonly record struct Velocity(int X, int Y, int Z);

public class WorldValidatorTests
{
    [Fact]
    public void EmptyWorld_IsValid()
    {
        using var world = new World();
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void WorldWithEntities_NoIssues()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Velocity(4, 5, 6));
        world.Create(new Position(7, 8), new Velocity(10, 11, 12));
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyAndRecreate_NoFalsePositives()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);
        world.Create(new Position(7, 8));
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void WorldWithHierarchy_NoIssues()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child = world.Create(new Position(1, 1));
        world.AddChild(parent, child);
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void MultipleArchetypes_NoIssues()
    {
        using var world = new World();
        for (var i = 0; i < 10; i++)
        {
            world.Create(new Position(i, 0));
            if (i % 2 == 0)
                world.Create(new Velocity(i, 0, 0));
        }
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyMany_NoIssues()
    {
        using var world = new World();
        var entities = new List<Entity>();
        for (var i = 0; i < 100; i++)
            entities.Add(world.Create(new Position(i, 0)));
        foreach (var e in entities)
            world.Destroy(e);
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyThenRecreateMany_NoIssues()
    {
        using var world = new World();
        var batch1 = new List<Entity>();
        for (var i = 0; i < 50; i++)
            batch1.Add(world.Create(new Position(i, 0)));
        foreach (var e in batch1)
            world.Destroy(e);

        for (var i = 0; i < 50; i++)
            world.Create(new Position(i * 10, 0));

        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyedEntity_NotInFreeList_FreeListCheckPasses()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Create(new Velocity(4, 5, 6));
        world.Destroy(e);
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }
}
