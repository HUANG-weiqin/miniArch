using MiniArch;
using Xunit;

namespace MiniArchTests.Core;

public class ChangeTrackingInfrastructureTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    private struct TransitionRecorder : ITransitionHandler
    {
        public System.Collections.Generic.List<(Entity, TransitionKind)> Changes;

        public TransitionRecorder(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, TransitionKind)>();
        }

        public void OnChange(World world, Entity entity, TransitionKind kind)
        {
            Changes.Add((entity, kind));
        }
    }

    private static TransitionWatch<TransitionRecorder> TrackPositions(World world)
    {
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = new TransitionRecorder(0);
        return watch;
    }

    [Fact]
    public void Create_appends_entered_transition()
    {
        using var world = new World();
        var watch = TrackPositions(world);
        watch.Snapshot(world);
        var e = world.Create(new Position(0, 0));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Create_then_destroy_produces_entered_then_exited()
    {
        using var world = new World();
        var watch = TrackPositions(world);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Destroy(e);
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Exited, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Add_produces_exited_when_filter_excludes_added_component()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>().Without<Velocity>());
        watch.Handler = new TransitionRecorder(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Add(e, new Velocity(0, 0));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Exited, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void Remove_produces_entered_when_excluded_component_removed()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>().Without<Velocity>());
        watch.Handler = new TransitionRecorder(0);
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        watch.Snapshot(world);
        world.Remove<Velocity>(e);
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(TransitionKind.Entered, change.Item2);
        Assert.Equal(e, change.Item1);
    }

    [Fact]
    public void No_transitions_when_tracking_inactive()
    {
        using var world = new World();
        world.Create(new Position(0, 0));
        var e = world.Create(new Position(0, 0));
        world.Add(e, new Velocity(0, 0));
        world.Destroy(e);

        // Track after the fact — watch starts empty
        var watch = TrackPositions(world);
        watch.Snapshot(world);
        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void Stale_watch_after_Dispose_throws()
    {
        var world = new World();
        var watch = TrackPositions(world);
        watch.Snapshot(world);
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => watch.Diff(world));
    }

    [Fact]
    public void Watch_empty_filter_throws()
    {
        using var world = new World();
        var ex = Assert.Throws<ArgumentException>(() =>
            world.Watch<TransitionRecorder>(new QueryDescription()));
        Assert.Contains("requires at least one component", ex.Message);
    }
}
