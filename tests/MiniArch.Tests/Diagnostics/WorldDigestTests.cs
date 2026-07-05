using MiniArch.Diagnostics;

namespace MiniArchTests.Diagnostics;

file readonly record struct Position(int X, int Y);
file readonly record struct Velocity(int X, int Y, int Z);

public class WorldDigestTests
{
    [Fact]
    public void EmptyWorld_DigestIsStable()
    {
        using var world = new World();
        var d1 = WorldDigest.Compute(world);
        var d2 = WorldDigest.Compute(world);
        Assert.Equal(d1.Total, d2.Total);
        Assert.Equal(d1.Occupancy, d2.Occupancy);
        Assert.Equal(d1.FreeList, d2.FreeList);
        Assert.Equal(d1.Hierarchy, d2.Hierarchy);
    }

    [Fact]
    public void SameWorlds_SameDigest()
    {
        using var w1 = new World();
        using var w2 = new World();
        w1.Create(new Position(1, 2), new Velocity(4, 5, 6));
        w2.Create(new Position(1, 2), new Velocity(4, 5, 6));
        var d1 = WorldDigest.Compute(w1);
        var d2 = WorldDigest.Compute(w2);
        Assert.Equal(d1.Total, d2.Total);
    }

    [Fact]
    public void SameOccupancy_SameOccupancyHash()
    {
        using var w1 = new World();
        using var w2 = new World();
        w1.Create(new Position(1, 2));
        w2.Create(new Position(4, 5)); // different value, same occupancy
        var d1 = WorldDigest.Compute(w1);
        var d2 = WorldDigest.Compute(w2);
        Assert.Equal(d1.Occupancy, d2.Occupancy);
        Assert.NotEqual(d1.PerComponent[typeof(Position)], d2.PerComponent[typeof(Position)]);
        Assert.NotEqual(d1.Total, d2.Total);
    }

    [Fact]
    public void ComponentValueChange_OnlyThatComponentHashChanges()
    {
        using var w1 = new World();
        using var w2 = new World();
        w1.Create(new Position(1, 2), new Velocity(10, 20, 30));
        w2.Create(new Position(1, 2), new Velocity(99, 99, 99));

        var d1 = WorldDigest.Compute(w1);
        var d2 = WorldDigest.Compute(w2);

        Assert.Equal(d1.PerComponent[typeof(Position)], d2.PerComponent[typeof(Position)]);
        Assert.NotEqual(d1.PerComponent[typeof(Velocity)], d2.PerComponent[typeof(Velocity)]);
    }

    [Fact]
    public void DestroyEntity_FreeListHashChanges()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        var d1 = WorldDigest.Compute(world);

        world.Destroy(e);
        var d2 = WorldDigest.Compute(world);

        Assert.NotEqual(d1.FreeList, d2.FreeList);
        Assert.NotEqual(d1.Occupancy, d2.Occupancy);
    }

    [Fact]
    public void HierarchyChange_HierarchyHashChanges()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child = world.Create(new Position(1, 1));
        var d1 = WorldDigest.Compute(world);

        world.AddChild(parent, child);
        var d2 = WorldDigest.Compute(world);

        Assert.NotEqual(d1.Hierarchy, d2.Hierarchy);
        Assert.NotEqual(d1.Total, d2.Total);
        Assert.Equal(d1.Occupancy, d2.Occupancy);
    }

    [Fact]
    public void DifferentEntityCount_DifferentOccupancy()
    {
        using var w1 = new World();
        using var w2 = new World();
        w1.Create(new Position(1, 2));
        w1.Create(new Position(4, 5));
        w2.Create(new Position(1, 2));
        var d1 = WorldDigest.Compute(w1);
        var d2 = WorldDigest.Compute(w2);
        Assert.NotEqual(d1.Occupancy, d2.Occupancy);
    }

    [Fact]
    public void EmptyWorld_HasEmptyPerComponent()
    {
        using var world = new World();
        var digest = WorldDigest.Compute(world);
        Assert.Empty(digest.PerComponent);
    }

    [Fact]
    public void MultipleComponents_AllHaveHashes()
    {
        using var world = new World();
        world.Create(new Position(1, 2), new Velocity(4, 5, 6));
        world.Create(new Position(7, 8));
        var digest = WorldDigest.Compute(world);
        Assert.Contains(typeof(Position), digest.PerComponent.Keys);
        Assert.Contains(typeof(Velocity), digest.PerComponent.Keys);
    }

    [Fact]
    public void ArchetypeHashes_NotEmpty()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Velocity(4, 5, 6));
        var digest = WorldDigest.Compute(world);
        Assert.NotEmpty(digest.PerArchetype);
    }
}
