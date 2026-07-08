using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

public class ChangeTrackingInfrastructureTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    private static TransitionLog TrackPositions(World world)
    {
        return world.TrackTransitions(new QueryDescription().With<Position>());
    }

    // ── structural transition dispatch ───────────────────────

    [Fact]
    public void Create_appends_entered_transition()
    {
        using var world = new World();
        var log = TrackPositions(world);
        var e = world.Create(new Position(0, 0));
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Create_then_destroy_produces_entered_then_exited()
    {
        using var world = new World();
        var log = TrackPositions(world);
        var e = world.Create(new Position(0, 0));
        log.Clear();  // drain create transition
        world.Destroy(e);
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Add_produces_exited_when_filter_excludes_added_component()
    {
        using var world = new World();
        var log = world.TrackTransitions(new QueryDescription().With<Position>().Without<Velocity>());
        var e = world.Create(new Position(0, 0));
        log.Clear();  // drain create
        world.Add(e, new Velocity(0, 0));
        // Entity left {Position, !Velocity} → Exited
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Remove_produces_entered_when_excluded_component_removed()
    {
        using var world = new World();
        var log = world.TrackTransitions(new QueryDescription().With<Position>().Without<Velocity>());
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        log.Clear();  // drain create (entity does not match filter — has Velocity)
        world.Remove<Velocity>(e);
        // Entity entered {Position, !Velocity} → Entered
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Add_existing_component_does_not_append_transition()
    {
        using var world = new World();
        var log = TrackPositions(world);
        var e = world.Create(new Position(1, 1));
        log.Clear();  // drain
        Assert.Throws<InvalidOperationException>(() => world.Add(e, new Position(2, 2)));
        Assert.Empty(log.Transitions.ToArray());  // no transition produced (never reached the write)
    }

    [Fact]
    public void Clone_appends_entered_transition()
    {
        using var world = new World();
        var log = TrackPositions(world);
        var src = world.Create(new Position(7, 7));
        log.Clear();  // drain create
        var clone = world.Clone(src);
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(clone, ts[0].Entity);
        Assert.NotEqual(src, clone);
    }

    [Fact]
    public void No_transitions_when_tracking_inactive()
    {
        using var world = new World();   // no Track
        world.Create(new Position(0, 0));
        var e = world.Create(new Position(0, 0));
        world.Add(e, new Velocity(0, 0));
        world.Destroy(e);

        // Track after the fact — no dispatches happened before Track
        var log = TrackPositions(world);
        Assert.Empty(log.Transitions.ToArray());
    }

    [Fact]
    public void TransitionLog_stale_after_Dispose_throws()
    {
        var world = new World();
        var log = TrackPositions(world);

        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = log.Transitions);
    }

    [Fact]
    public void TrackTransitions_empty_filter_throws()
    {
        using var world = new World();
        var ex = Assert.Throws<ArgumentException>(() =>
            world.TrackTransitions(new QueryDescription()));
        Assert.Contains("requires at least one component", ex.Message);
    }
}
