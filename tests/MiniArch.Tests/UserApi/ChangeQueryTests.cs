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
    public void ValueChanges_span_is_valid_until_next_Clear()
    {
        var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        _ = q.ValueChanges<Position>();

        world.Set(e, new Position(1, 1));
        var changes = q.ValueChanges<Position>();

        // Span is non-destructive: repeated calls return same data
        var changes2 = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(1, changes2.Length);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(1, 1), changes[0].New);

        // After ClearChanges, span data is stale (but still readable)
        q.ClearChanges<Position>();
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
    public void Capture_only_without_Previous_is_inert()
    {
        var world = new World();

        // Capture<Position>() without Previous and without filter — should NOT
        // create a shared tracker and should NOT register for transitions.
        var captureOnly = world.Track().Capture<Position>();

        // Active query with Previous should still work independently.
        var posQ = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(1, 2));

        _ = posQ.ValueChanges<Position>();
        world.Set(e, new Position(10, 20));

        // capture-only never registered for transitions — calling Transitions
        // on it should return empty and not interfere with posQ's data.
        Assert.Empty(captureOnly.Transitions());

        // posQ should still see the change
        var changes = posQ.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(new Position(1, 2), changes[0].Old);
        Assert.Equal(new Position(10, 20), changes[0].New);
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

        // Verify the shared tracker's internal buffer grew past e1.Id
        var tracker = GetSharedTracker<Position>(world);
        Assert.NotNull(tracker);
        Assert.True(tracker!.SlotByEntityPlusOne.Length > e1.Id);
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

    // ── Shared tracker: non-destructive Read + explicit Clear ────

    [Fact]
    public void ValueChanges_is_non_destructive_multiple_calls_return_same_data()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        var span1 = q.ValueChanges<Position>();
        var span2 = q.ValueChanges<Position>();

        Assert.Equal(span1.Length, span2.Length);
        Assert.Equal(1, span1.Length);
        Assert.Equal(span1[0].Entity, span2[0].Entity);
        Assert.Equal(span1[0].New.X, span2[0].New.X);
    }

    [Fact]
    public void Explicit_clear_empties_value_changes()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, q.ValueChanges<Position>().Length);

        q.ClearChanges<Position>();

        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void ClearChanges_after_no_changes_is_noop()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, q.ValueChanges<Position>().Length);
        q.ClearChanges<Position>();
        Assert.Equal(0, q.ValueChanges<Position>().Length);
        q.ClearChanges<Position>();  // second clear, no changes present
        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void World_ClearChanges_clears_all_value_changes()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, q.ValueChanges<Position>().Length);

        world.ClearChanges<Position>();

        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void After_clear_new_set_produces_fresh_entry()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, q.ValueChanges<Position>().Length);
        q.ClearChanges<Position>();

        world.Set(e, new Position(30, 40));

        var span = q.ValueChanges<Position>();
        Assert.Equal(1, span.Length);
        Assert.Equal(30, span[0].New.X);
    }

    [Fact]
    public void Two_queries_same_component_type_see_identical_changes()
    {
        using var world = new World();
        var q1 = world.Track().Capture<Position>().Previous();
        var q2 = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        var span1 = q1.ValueChanges<Position>();
        var span2 = q2.ValueChanges<Position>();

        Assert.Equal(span1.Length, span2.Length);
        Assert.Equal(1, span1.Length);
        Assert.Equal(span1[0].Entity, span2[0].Entity);
        Assert.Equal(span1[0].New.X, span2[0].New.X);
    }

    [Fact]
    public void ClearChanges_on_one_query_clears_shared_tracker_for_all_same_type_queries()
    {
        using var world = new World();
        var q1 = world.Track().Capture<Position>().Previous();
        var q2 = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20));
        Assert.Equal(1, q1.ValueChanges<Position>().Length);
        Assert.Equal(1, q2.ValueChanges<Position>().Length);

        q1.ClearChanges<Position>();

        Assert.Equal(0, q1.ValueChanges<Position>().Length);
        Assert.Equal(0, q2.ValueChanges<Position>().Length);
    }

    [Fact]
    public void Set_records_once_per_component_type_regardless_of_consumer_count()
    {
        using var world = new World();
        var q1 = world.Track().Capture<Position>().Previous();
        var q2 = world.Track().Capture<Position>().Previous();
        var q3 = world.Track().Capture<Position>().Previous();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20));

        // All three see exactly one change
        Assert.Equal(1, q1.ValueChanges<Position>().Length);
        Assert.Equal(1, q2.ValueChanges<Position>().Length);
        Assert.Equal(1, q3.ValueChanges<Position>().Length);
    }

    [Fact]
    public void Filter_disables_typed_fast_path_value_changes_empty()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().With<Position>().Previous();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        // Filter present → typed fast path disabled → no value changes
        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void Second_capture_disables_typed_fast_path()
    {
        using var world = new World();
        var q = world.Track().Capture<Position>().Capture<Velocity>().Previous();
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));
        world.Set(e, new Position(10, 20));

        // Two captures → typed fast path disabled → ValueChanges<Position> empty
        Assert.Equal(0, q.ValueChanges<Position>().Length);
    }

    [Fact]
    public void Destroy_and_id_reuse_clears_old_change_entry()
    {
        using var world = new World(entityCapacity: 1);
        var q = world.Track().Capture<Position>().Previous();
        var e1 = world.Create(new Position(1, 2));
        var oldId = e1.Id;

        world.Set(e1, new Position(10, 20));
        Assert.Equal(1, q.ValueChanges<Position>().Length);
        q.ClearChanges<Position>();

        world.Destroy(e1);
        // Reuse the same slot
        var e2 = world.Create(new Position(100, 200));
        Assert.Equal(oldId, e2.Id);  // same slot, different version

        world.Set(e2, new Position(300, 400));
        var span = q.ValueChanges<Position>();
        Assert.Equal(1, span.Length);
        Assert.Equal(300, span[0].New.X);   // new entity's change, not old
        Assert.Equal(e2, span[0].Entity); // verify correct entity
    }

    [Fact]
    public void Different_component_types_remain_independent()
    {
        using var world = new World();
        var posQ = world.Track().Capture<Position>().Previous();
        var velQ = world.Track().Capture<Velocity>().Previous();
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        world.Set(e, new Position(10, 20));
        world.Set(e, new Velocity(30, 40));

        Assert.Equal(1, posQ.ValueChanges<Position>().Length);
        Assert.Equal(1, velQ.ValueChanges<Velocity>().Length);

        posQ.ClearChanges<Position>();
        Assert.Equal(0, posQ.ValueChanges<Position>().Length);
        Assert.Equal(1, velQ.ValueChanges<Velocity>().Length);  // velQ unaffected

        velQ.ClearChanges<Velocity>();
        Assert.Equal(0, velQ.ValueChanges<Velocity>().Length);
    }

    // ── Bug fix: Previous() before Capture<T> order ─────────────────────

    [Fact]
    public void ValueChanges_previous_before_capture_order_tracks_values()
    {
        using var world = new World();
        // Previous() before Capture<T> — tracker must still be created
        var q = world.Track().Previous().Capture<Position>();
        var e = world.Create(new Position(0, 0));

        _ = q.ValueChanges<Position>();
        world.Set(e, new Position(1, 1));

        var changes = q.ValueChanges<Position>();
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(1, 1), changes[0].New);
    }

    // ── Bug fix: ValueChanges scoped to captured type ───────────────────

    [Fact]
    public void ValueChanges_for_uncaptured_type_returns_empty_even_when_other_tracker_exists()
    {
        using var world = new World();
        var posQ = world.Track().Capture<Position>().Previous();
        var velQ = world.Track().Capture<Velocity>().Previous();
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        // Drain baselines
        _ = posQ.ValueChanges<Position>();
        _ = velQ.ValueChanges<Velocity>();

        // Set Velocity — only velQ should see the change
        world.Set(e, new Velocity(3, 4));

        // posQ did NOT capture Velocity → must return empty
        var posChanges = posQ.ValueChanges<Velocity>();
        Assert.Equal(0, posChanges.Length);

        // velQ captured Velocity → must see the change
        var velChanges = velQ.ValueChanges<Velocity>();
        Assert.Equal(1, velChanges.Length);
        Assert.Equal(e, velChanges[0].Entity);
        Assert.Equal(new Velocity(1, 2), velChanges[0].Old);
        Assert.Equal(new Velocity(3, 4), velChanges[0].New);
    }

    // ── Bug fix: ClearChanges scoped to captured type ───────────────────

    [Fact]
    public void ClearChanges_for_uncaptured_type_does_not_clear_other_tracker()
    {
        using var world = new World();
        var posQ = world.Track().Capture<Position>().Previous();
        var velQ = world.Track().Capture<Velocity>().Previous();
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        // Drain baselines
        _ = posQ.ValueChanges<Position>();
        _ = velQ.ValueChanges<Velocity>();

        // Set Velocity
        world.Set(e, new Velocity(3, 4));
        Assert.Equal(1, velQ.ValueChanges<Velocity>().Length);

        // posQ.ClearChanges<Velocity>() should be a no-op (posQ captured Position)
        posQ.ClearChanges<Velocity>();
        Assert.Equal(1, velQ.ValueChanges<Velocity>().Length); // velQ still sees its change

        // world.ClearChanges<Velocity>() is global — should clear it
        world.ClearChanges<Velocity>();
        Assert.Equal(0, velQ.ValueChanges<Velocity>().Length);
    }

    // ── Bug fix: World.ClearChanges throws after dispose ────────────────

    [Fact]
    public void World_ClearChanges_throws_after_dispose()
    {
        var world = new World();
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => world.ClearChanges<Position>());
    }

    // ── Pre-size check: tracker created after many entities is pre-sized ─

    [Fact]
    public void Shared_tracker_PreSize_called_on_creation()
    {
        using var world = new World(entityCapacity: 64);
        // Create enough entities to grow entity capacity beyond the default 64
        for (var i = 0; i < 65; i++)
            world.Create(new Position(i, i));

        var capacity = world.EntityCapacity;
        Assert.True(capacity > 64, $"EntityCapacity should have grown past 64, was {capacity}");

        // Activate query — this should create the shared tracker and PreSize it
        var q = world.Track().Capture<Position>().Previous();

        var tracker = GetSharedTracker<Position>(world);
        Assert.NotNull(tracker);
        // SlotByEntityPlusOne should be pre-sized to at least entity capacity
        Assert.True(tracker!.SlotByEntityPlusOne.Length >= capacity,
            $"SlotByEntityPlusOne.Length={tracker.SlotByEntityPlusOne.Length} < EntityCapacity={capacity}");
    }

    [Fact]
    public void Shared_tracker_PreSize_called_when_reused_after_world_growth()
    {
        using var world = new World(entityCapacity: 64);

        _ = world.Track().Capture<Position>().Previous();
        var tracker = GetSharedTracker<Position>(world);
        Assert.NotNull(tracker);
        Assert.True(tracker!.SlotByEntityPlusOne.Length <= 64,
            $"Expected initial tracker size near default, was {tracker.SlotByEntityPlusOne.Length}");

        for (var i = 0; i < 65; i++)
            world.Create(new Position(i, i));

        var capacity = world.EntityCapacity;
        Assert.True(capacity > 64, $"EntityCapacity should have grown past 64, was {capacity}");

        _ = world.Track().Capture<Position>().Previous();

        Assert.True(tracker.SlotByEntityPlusOne.Length >= capacity,
            $"SlotByEntityPlusOne.Length={tracker.SlotByEntityPlusOne.Length} < EntityCapacity={capacity}");
    }

    private static ChangeTracker<T>? GetSharedTracker<T>(World world) where T : unmanaged
    {
        var registry = typeof(World).GetField("SharedTrackers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(registry);
        var reg = registry.GetValue(world);
        var method = reg!.GetType().GetMethod("GetTracker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (ChangeTracker<T>?)method!.MakeGenericMethod(typeof(T)).Invoke(reg, null);
    }
}
