using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

public class ChangeTrackingInfrastructureTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    // ── structural transition dispatch ───────────────────────

    [Fact]
    public void Create_appends_entered_transition()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0));
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Create_then_destroy_produces_entered_then_exited()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0));
        pos.Transitions();  // drain create transition
        world.Destroy(e);
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Add_produces_exited_when_filter_excludes_added_component()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>().Without<Velocity>();
        var e = world.Create(new Position(0, 0));
        pos.Transitions();  // drain create
        world.Add(e, new Velocity(0, 0));
        // Entity left {Position, !Velocity} → Exited
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Remove_produces_entered_when_excluded_component_removed()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>().Without<Velocity>();
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        pos.Transitions();  // drain create (entity does not match filter — has Velocity)
        world.Remove<Velocity>(e);
        // Entity entered {Position, !Velocity} → Entered
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Add_existing_component_does_not_append_transition()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(1, 1));
        pos.Transitions();  // drain
        Assert.Throws<InvalidOperationException>(() => world.Add(e, new Position(2, 2)));
        Assert.Empty(pos.Transitions());  // no transition produced (never reached the write)
    }

    [Fact]
    public void Clone_appends_entered_transition()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>();
        var src = world.Create(new Position(7, 7));
        pos.Transitions();  // drain create
        var clone = world.Clone(src);
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(clone, ts[0].Entity);
        Assert.NotEqual(src, clone);
    }

    [Fact]
    public void No_transitions_when_tracking_inactive()
    {
        var world = new World();   // no Track
        world.Create(new Position(0, 0));
        var e = world.Create(new Position(0, 0));
        world.Add(e, new Velocity(0, 0));
        world.Destroy(e);

        // Track after the fact — no dispatches happened before Track
        var pos = world.Track().Capture<Position>().With<Position>();
        Assert.Empty(pos.Transitions());
    }

    [Fact]
    public void ChangeQuery_stale_cursor_after_Dispose_throws()
    {
        var world = new World();
        var q = world.Track().Capture<Position>();

        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => q.Transitions());
    }
}
