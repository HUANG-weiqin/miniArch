using MiniArch.Diagnostics;

namespace MiniArchTests.Diagnostics;

file readonly record struct Position(int X, int Y);
file readonly record struct Velocity(int X, int Y, int Z);

public class EntityDumpTests
{
    [Fact]
    public void Describe_DeadEntity_ReturnsDead()
    {
        using var world = new World();
        var dead = new Entity(999, 0);
        var report = EntityDump.Describe(world, dead);
        Assert.False(report.IsAlive);
        Assert.Equal(999, report.Id);
    }

    [Fact]
    public void Describe_DestroyedEntity_ReturnsDead()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);
        var report = EntityDump.Describe(world, e);
        Assert.False(report.IsAlive);
    }

    [Fact]
    public void Describe_AliveEntity_HasComponents()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2), new Velocity(4, 5, 6));
        var report = EntityDump.Describe(world, e);
        Assert.True(report.IsAlive);
        Assert.NotNull(report.Archetype);
        Assert.Equal(2, report.Components.Count);
        Assert.Contains(report.Components, c => c.Type == typeof(Position));
        Assert.Contains(report.Components, c => c.Type == typeof(Velocity));
    }

    [Fact]
    public void Describe_RawBytesMatchStructSize()
    {
        using var world = new World();
        var e = world.Create(new Position(10, 20));
        var report = EntityDump.Describe(world, e);
        var posInfo = report.Components[0];
        Assert.Equal(8, posInfo.SizeBytes); // Position: 2 ints = 8 bytes
        Assert.NotNull(posInfo.RawBytes);
        Assert.Equal(8, posInfo.RawBytes!.Length);
    }

    [Fact]
    public void Describe_Hierarchy_HasParentAndChildren()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child1 = world.Create(new Position(1, 1));
        var child2 = world.Create(new Position(2, 2));
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

        var parentReport = EntityDump.Describe(world, parent);
        Assert.True(parentReport.IsAlive);
        Assert.Null(parentReport.Parent);
        Assert.Equal(2, parentReport.Children.Count);

        var childReport = EntityDump.Describe(world, child1);
        Assert.True(childReport.IsAlive);
        Assert.NotNull(childReport.Parent);
        Assert.Equal(parent, childReport.Parent!.Value);
    }

    [Fact]
    public void Describe_ToString_NoException()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        var report = EntityDump.Describe(world, e);
        var str = report.ToString();
        Assert.Contains("ALIVE", str);
        Assert.Contains(nameof(Position), str);
    }

    [Fact]
    public void Describe_PlaceholderEntity_ReturnsDead()
    {
        using var world = new World();
        var placeholder = new Entity(-1, 0);
        var report = EntityDump.Describe(world, placeholder);
        Assert.False(report.IsAlive);
    }

    [Fact]
    public void Describe_OutOfBoundsId_ReturnsDead()
    {
        using var world = new World();
        var bogus = new Entity(999_999, 0);
        var report = EntityDump.Describe(world, bogus);
        Assert.False(report.IsAlive);
    }
}
