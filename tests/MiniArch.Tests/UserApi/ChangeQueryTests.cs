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
        var hp = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        _ = hp.ModifiedChunks();                 // drain any setup noise
        world.Set(e, new Position(1, 1));
        var modified = hp.ModifiedChunks().ToList();
        Assert.NotEmpty(modified);
    }

    [Fact]
    public void ModifiedChunks_empty_when_no_write_since_last_call()
    {
        var world = new World();
        var hp = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(1, 1));
        Assert.NotEmpty(hp.ModifiedChunks());    // first call sees the write
        Assert.Empty(hp.ModifiedChunks());        // second call: cursor advanced, nothing new
    }

    [Fact]
    public void ModifiedChunks_does_not_yield_for_unrelated_component_write()
    {
        var world = new World();
        var pos = world.Track<Position>();
        var e = world.Create(new Position(0, 0), new Velocity(0, 0));
        _ = pos.ModifiedChunks();
        world.Set(e, new Velocity(9, 9));        // Velocity written, not Position
        Assert.Empty(pos.ModifiedChunks());
    }

    // ── Transitions ────────────────────────────────────────────────

    [Fact]
    public void Transitions_yields_entered_on_create()
    {
        var world = new World();
        var hp = world.Track<Position>();
        var e = world.Create(new Position(0, 0));
        var ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void Transitions_yields_exited_on_destroy()
    {
        var world = new World();
        var hp = world.Track<Position>();
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
        var pos = world.Track<Position>();
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
        var pos = world.Track<Position>();
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
        var vel = world.Track<Velocity>();
        var e = world.Create(new Velocity(1, 1));
        _ = vel.Transitions();
        world.Remove<Velocity>(e);                // Exited
        world.Add(e, new Velocity(2, 2));         // Entered
        var ts = vel.Transitions().ToList();
        Assert.Equal(2, ts.Count);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(TransitionKind.Entered, ts[1].Kind);
    }

    [Fact]
    public void Transitions_destroy_then_recreate_are_distinct_with_old_version()
    {
        var world = new World();
        var hp = world.Track<Position>();
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
        var hp = world.Track<Position>();
        world.Create(new Position(1, 1));
        _ = hp.Transitions();
        Assert.Empty(hp.Transitions());
    }
}
