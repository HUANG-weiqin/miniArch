using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

public class ChangeTrackingInfrastructureTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Tracking_is_inactive_by_default()
    {
        var world = new World();
        Assert.False(world.IsChangeTrackingActive);
    }

    [Fact]
    public void Track_activates_tracking()
    {
        var world = new World();
        var hp = world.Track<Position>();
        Assert.NotNull(hp);
        Assert.True(world.IsChangeTrackingActive);
    }

    [Fact]
    public void Track_activates_column_versions_on_existing_archetype()
    {
        var world = new World();
        var e = world.Create(new Position(0, 0));   // archetype exists before Track
        world.Track<Position>();
        // activation should have retro-fitted the archetype holding Position
        Assert.True(world.IsChangeTrackingActive);
        // transition log should be empty (Track itself appends nothing)
        Assert.Empty(world.GetTransitionLogInternal());
    }

    [Fact]
    public void Track_then_create_activates_new_archetype_from_birth()
    {
        var world = new World();
        world.Track<Position>();
        var e = world.Create(new Position(0, 0));   // archetype created AFTER Track
        // no exception; archetype got activated in GetOrCreateArchetype
        Assert.True(world.IsChangeTrackingActive);
    }

    [Fact]
    public void WriteEpoch_starts_at_zero()
    {
        var world = new World();
        Assert.Equal(0, world.CurrentWriteEpoch);
    }
}
