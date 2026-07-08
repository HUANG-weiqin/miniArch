using System.Reflection;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.UserApi;

public class ChangeQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);

    [Fact]
    public void ValueChanges_single_capture_keeps_first_old_and_last_new()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.ValueChanges<Position>();

        world.Set(e, new Position(1, 1));
        world.Set(e, new Position(2, 3));

        var changes = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(2, 3), changes[0].New);
    }

    [Fact]
    public void ValueChanges_span_survives_next_Set_until_next_drain()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.ValueChanges<Position>();

        world.Set(e, new Position(1, 1));
        var changes = q.ValueChanges<Position>();

        world.Set(e, new Position(2, 2));

        Assert.Equal(1, changes.Length);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(1, 1), changes[0].New);
    }

    [Fact]
    public void BUG_ValueChanges_captures_CommandStream_Set_writes()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        _ = q.ValueChanges<Position>();

        stream.Set(e, new Position(4, 5));
        Assert.True(stream.Submit());

        var changes = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(4, 5), changes[0].New);
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

    // ── Multi-query ValueChanges ──────────────────────────────────────

    [Fact]
    public void ValueChanges_two_queries_different_types_both_capture()
    {
        var world = new World();

        var posQ = world.Track().Capture<Position>().Previous();
        var velQ = world.Track().Capture<Velocity>().Previous();

        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        // Drain: establish baseline
        _ = posQ.ValueChanges<Position>();
        _ = velQ.ValueChanges<Velocity>();

        // Set both
        world.Set(e, new Position(10, 20));
        world.Set(e, new Velocity(30, 40));

        var posChanges = posQ.ValueChanges<Position>();
        var velChanges = velQ.ValueChanges<Velocity>();

        Assert.Equal(1, posChanges.Length);
        Assert.Equal(new Position(0, 0), posChanges[0].Old);
        Assert.Equal(new Position(10, 20), posChanges[0].New);
        Assert.Equal(e, posChanges[0].Entity);

        Assert.Equal(1, velChanges.Length);
        Assert.Equal(new Velocity(1, 2), velChanges[0].Old);
        Assert.Equal(new Velocity(30, 40), velChanges[0].New);
        Assert.Equal(e, velChanges[0].Entity);
    }

    [Fact]
    public void ValueChanges_two_queries_same_type_both_capture()
    {
        var world = new World();

        var q1 = world.Track().Capture<Position>().Previous();
        var q2 = world.Track().Capture<Position>().Previous();

        var e = world.Create(new Position(0, 0));

        // Drain baseline
        _ = q1.ValueChanges<Position>();
        _ = q2.ValueChanges<Position>();

        world.Set(e, new Position(99, 99));

        var c1 = q1.ValueChanges<Position>();
        var c2 = q2.ValueChanges<Position>();

        Assert.Equal(1, c1.Length);
        Assert.Equal(new Position(0, 0), c1[0].Old);
        Assert.Equal(new Position(99, 99), c1[0].New);

        Assert.Equal(1, c2.Length);
        Assert.Equal(new Position(0, 0), c2[0].Old);
        Assert.Equal(new Position(99, 99), c2[0].New);
    }

    [Fact]
    public void ValueChanges_nontracking_query_does_not_interfere()
    {
        var world = new World();

        var posQ = world.Track().Capture<Position>().Previous();
        var unusedQ = world.Track();  // no Capture, no Previous — should not interfere
        var velQ = world.Track().Capture<Velocity>().Previous();

        var e = world.Create(new Position(1, 2), new Velocity(3, 4));

        _ = posQ.ValueChanges<Position>();
        _ = velQ.ValueChanges<Velocity>();

        world.Set(e, new Position(5, 6));
        world.Set(e, new Velocity(7, 8));

        var posChanges = posQ.ValueChanges<Position>();
        var velChanges = velQ.ValueChanges<Velocity>();

        Assert.Equal(1, posChanges.Length);
        Assert.Equal(1, velChanges.Length);
        Assert.Equal(new Position(1, 2), posChanges[0].Old);
        Assert.Equal(new Velocity(3, 4), velChanges[0].Old);
    }

    [Fact]
    public void ValueChanges_value_only_query_does_not_collect_transitions()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        Assert.Empty(q.Transitions());

        world.Destroy(e);

        Assert.Empty(q.Transitions());
    }

    [Fact]
    public void ValueChanges_query_that_adds_filter_collects_transitions_again()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous().With<Position>();

        var e = world.Create(new Position(0, 0));

        var ts = q.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void BUG_ValueChanges_query_survives_RestoreState_without_stale_or_lost_tracking()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.ValueChanges<Position>();

        var snapshot = world.CaptureState();
        world.Set(e, new Position(1, 1));
        world.RestoreState(snapshot);

        Assert.Equal(0, q.ValueChanges<Position>().Length);

        world.Set(e, new Position(2, 2));

        var changes = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(2, 2), changes[0].New);
    }

    [Fact]
    public void BUG_ValueChanges_deactivates_when_filter_is_added_after_activation()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous().With<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(1, 1));

        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void BUG_ValueChanges_deactivates_when_second_capture_is_added_after_activation()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous().Capture<Velocity>();
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        world.Set(e, new Position(3, 4));

        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void BUG_ValueChanges_handles_world_growth_after_query_creation()
    {
        var world = new World(entityCapacity: 64);
        var q = world.Track().Capture<Position>().Previous();

        _ = q.ValueChanges<Position>();

        Entity e1 = default;
        for (var i = 0; i < 65; i++)
            e1 = world.Create(new Position(i, i));

        Assert.True(world.EntityCapacity > 64);

        world.Set(e1, new Position(11, 11));

        var changes = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e1, changes[0].Entity);
        Assert.Equal(new Position(64, 64), changes[0].Old);
        Assert.Equal(new Position(11, 11), changes[0].New);

        var tracker = GetTypedTracker<Position>(q);
        Assert.True(tracker.SlotByEntityPlusOne.Length > e1.Id);
    }

    [Fact]
    public void BUG_ValueChanges_does_not_merge_destroyed_entity_with_reused_id()
    {
        var world = new World(entityCapacity: 1);
        var q = world.Track().Capture<Position>().Previous();
        var e1 = world.Create(new Position(0, 0));

        _ = q.ValueChanges<Position>();

        world.Set(e1, new Position(1, 1));
        world.Destroy(e1);

        var e2 = world.Create(new Position(100, 100));
        Assert.Equal(e1.Id, e2.Id);
        Assert.NotEqual(e1, e2);

        world.Set(e2, new Position(101, 101));

        var changes = q.ValueChanges<Position>();
        Assert.Equal(2, changes.Length);

        Assert.Equal(e1, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(1, 1), changes[0].New);

        Assert.Equal(e2, changes[1].Entity);
        Assert.Equal(new Position(100, 100), changes[1].Old);
        Assert.Equal(new Position(101, 101), changes[1].New);
    }

    private static ChangeTracker<T> GetTypedTracker<T>(ChangeQuery query) where T : unmanaged
    {
        var field = typeof(ChangeQuery).GetField("_typedTracker", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var tracker = field.GetValue(query);
        Assert.IsType<ChangeTracker<T>>(tracker);
        return (ChangeTracker<T>)tracker;
    }
}
