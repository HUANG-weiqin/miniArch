using MiniArch;
using Xunit;

namespace MiniArchTests.UserApi;

public class ChangeQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);

    // ── ModifiedChunks ─────────────────────────────────────────────

    [Fact]
    public void ModifiedChunks_yields_chunk_after_Set()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0));
        _ = q.ModifiedChunks<Position>();                 // drain any setup noise
        world.Set(e, new Position(1, 1));
        var modified = q.ModifiedChunks<Position>().ToList();
        Assert.NotEmpty(modified);
    }

    [Fact]
    public void ModifiedChunks_empty_when_no_write_since_last_call()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(1, 1));
        Assert.NotEmpty(q.ModifiedChunks<Position>());    // first call sees the write
        Assert.Empty(q.ModifiedChunks<Position>());        // second call: cursor advanced, nothing new
    }

    [Fact]
    public void ModifiedChunks_does_not_yield_for_unrelated_component_write()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        _ = q.ModifiedChunks<Position>();
        world.Set(e, new Velocity(9, 9));        // Velocity written, not Position
        Assert.Empty(q.ModifiedChunks<Position>());
    }

    [Fact]
    public void Changes_single_capture_without_filter_reports_Set()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.Changes();

        world.Set(e, new Position(1, 2));

        var changes = q.Changes();
        Assert.Single(changes);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old.Get<Position>());
        Assert.Equal(new Position(1, 2), changes[0].New.Get<Position>());
    }

    [Fact]
    public void DrainTypedChanges_single_capture_keeps_first_old_and_last_new()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.DrainTypedChanges<Position>();

        world.Set(e, new Position(1, 1));
        world.Set(e, new Position(2, 3));

        var changes = q.DrainTypedChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(2, 3), changes[0].New);
    }

    [Fact]
    public void DrainTypedChanges_span_survives_next_Set_until_next_drain()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.DrainTypedChanges<Position>();

        world.Set(e, new Position(1, 1));
        var changes = q.DrainTypedChanges<Position>();

        world.Set(e, new Position(2, 2));

        Assert.Equal(1, changes.Length);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(1, 1), changes[0].New);
    }

    // ── Transitions ────────────────────────────────────────────────

    [Fact]
    public void Transitions_yields_entered_on_create()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0));
        var ts = q.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Transitions_yields_exited_on_destroy()
    {
        var world = new World();
        var hp = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(0, 0));
        _ = hp.Transitions();                    // drain create
        world.Destroy(e);
        var ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Transitions_yields_entered_on_add_of_tracked_component()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>();
        var e = world.Create();                   // empty entity, no Position
        _ = pos.Transitions();                    // drain (no Position transition yet)
        world.Add(e, new Position(1, 1));         // gains Position -> Entered
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Transitions_yields_exited_on_remove_of_tracked_component()
    {
        var world = new World();
        var pos = world.Track().Capture<Position>().With<Position>();
        var e = world.Create(new Position(1, 1));
        _ = pos.Transitions();
        world.Remove<Position>(e);                // loses Position -> Exited
        var ts = pos.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Transitions_preserves_remove_then_add_order()
    {
        var world = new World();
        var q = world.Track().Capture<Velocity>().With<Velocity>();
        var e = world.Create(new Velocity(1, 1));
        _ = q.Transitions();
        world.Remove<Velocity>(e);                // Exited
        world.Add(e, new Velocity(2, 2));         // Entered
        var ts = q.Transitions().ToList();
        Assert.Equal(2, ts.Count);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(TransitionKind.Entered, ts[1].Kind);
    }

    [Fact]
    public void Transitions_destroy_then_recreate_are_distinct_with_old_version()
    {
        var world = new World();
        var hp = world.Track().Capture<Position>().With<Position>();
        var e1 = world.Create(new Position(1, 1));
        _ = hp.Transitions();
        world.Destroy(e1);
        var e2 = world.Create(new Position(2, 2));
        var ts = hp.Transitions().ToList();
        Assert.Equal(2, ts.Count);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e1, ts[0].Entity);
        Assert.Equal(TransitionKind.Entered, ts[1].Kind);
        Assert.Equal(e2, ts[1].Entity);
        Assert.NotEqual(e1, e2);                  // version differs
    }

    [Fact]
    public void Transitions_empty_after_drain_with_no_new_changes()
    {
        var world = new World();
        var hp = world.Track().Capture<Position>().With<Position>();
        world.Create(new Position(1, 1));
        _ = hp.Transitions();
        Assert.Empty(hp.Transitions());
    }
}
