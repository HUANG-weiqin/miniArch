using Xunit;

namespace MiniArchTests.UserApi;

public class ChangeQueryFilterTests
{
    // ── Test structs ────────────────────────────────────────────────
    private readonly record struct HP(int Value);
    private readonly record struct Dead;
    private readonly record struct Enemy;
    private readonly record struct Position(int X, int Y);

    // ── Without ────────────────────────────────────────────────────

    [Fact]
    public void Without_basic_transition()
    {
        var world = new World();
        var hp = world.Track<HP>().Without<Dead>();

        var e = world.Create(new HP(100));
        var ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);

        world.Add(e, new Dead());
        ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);

        world.Remove<Dead>(e);
        ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    // ── With ───────────────────────────────────────────────────────

    [Fact]
    public void With_filter_narrows_membership()
    {
        var world = new World();
        var hp = world.Track<HP>().With<Enemy>();

        // Entity with HP but no Enemy → does not match filter
        var e = world.Create(new HP(100));
        var ts = hp.Transitions().ToList();
        Assert.Empty(ts);

        // Add Enemy → now matches
        world.Add(e, new Enemy());
        ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);

        // Remove Enemy → no longer matches
        world.Remove<Enemy>(e);
        ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    // ── Without filters ModifiedChunks ─────────────────────────────

    [Fact]
    public void Without_filters_modified_chunks()
    {
        var world = new World();
        var filtered = world.Track<HP>().Without<Dead>();
        var unfiltered = world.Track<HP>();

        var e = world.Create(new HP(100));
        _ = filtered.ModifiedChunks();    // drain
        _ = unfiltered.ModifiedChunks();  // drain

        world.Add(e, new Dead());
        world.Set(e, new HP(50));

        // Filtered query: entity moved to {HP, Dead} → excluded by Without<Dead>
        var filteredChunks = filtered.ModifiedChunks().ToList();
        Assert.Empty(filteredChunks);

        // Unfiltered query: sees the write in the {HP, Dead} archetype
        var unfilteredChunks = unfiltered.ModifiedChunks().ToList();
        Assert.NotEmpty(unfilteredChunks);
    }

    // ── Default filter backward compatibility ──────────────────────

    [Fact]
    public void Default_filter_backward_compatible()
    {
        var world = new World();
        var hp = world.Track<HP>();   // no fluent calls → default filter = With<HP>

        var e = world.Create(new HP(100));
        var ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);

        // Adding Dead does not remove HP → entity still has HP → default filter still matches
        world.Add(e, new Dead());
        ts = hp.Transitions().ToList();
        Assert.Empty(ts);  // no transition: entity has HP before and after
    }

    // ── Multiple tracked types, independent filters ────────────────

    [Fact]
    public void Multiple_tracked_types_independent_filters()
    {
        var world = new World();
        var filtered = world.Track<HP>().Without<Dead>();
        var unfiltered = world.Track<HP>();

        var e = world.Create(new HP(100));
        var ts1 = filtered.Transitions().ToList();
        var ts2 = unfiltered.Transitions().ToList();
        Assert.Single(ts1);  // Entered in filtered (no Dead)
        Assert.Single(ts2);  // Entered in unfiltered (has HP)

        world.Add(e, new Dead());
        ts1 = filtered.Transitions().ToList();
        ts2 = unfiltered.Transitions().ToList();
        Assert.Single(ts1);  // Exited from filtered (now has Dead)
        Assert.Empty(ts2);   // Unfiltered: HP still present, no transition
    }

    // ── Chained filters ────────────────────────────────────────────

    [Fact]
    public void Chained_filters()
    {
        var world = new World();
        // Track HP entities that also have Enemy but do NOT have Dead
        var query = world.Track<HP>().With<Enemy>().Without<Dead>();

        // Entity {HP, Enemy} → matches filter
        var e1 = world.Create(new HP(100), new Enemy());
        var ts = query.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e1, ts[0].Entity);

        // Entity {HP, Enemy, Dead} → excluded by Without<Dead>
        var e2 = world.Create(new HP(50), new Enemy(), new Dead());
        ts = query.Transitions().ToList();
        Assert.Empty(ts);

        // Entity {HP} alone → missing Enemy → does not match
        var e3 = world.Create(new HP(30));
        ts = query.Transitions().ToList();
        Assert.Empty(ts);

        // Entity {HP, Enemy, Position} → matches (Position is irrelevant)
        var e4 = world.Create(new HP(20), new Enemy(), new Position(1, 2));
        ts = query.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e4, ts[0].Entity);
    }

    // ── Auto-clear (replaces ClearTransitionLog) ────────────────────

    [Fact]
    public void Transitions_auto_clears_after_drain()
    {
        var world = new World();
        var hp = world.Track<HP>();

        // Create 3 entities with HP → cursor sees 3 Entered
        world.Create(new HP(10));
        world.Create(new HP(20));
        world.Create(new HP(30));
        var ts = hp.Transitions().ToList();
        Assert.Equal(3, ts.Count);
        Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));

        // Create 2 more entities with HP → auto-cleared after first drain,
        // so cursor should see only 2 (not 5)
        world.Create(new HP(40));
        world.Create(new HP(50));
        ts = hp.Transitions().ToList();
        Assert.Equal(2, ts.Count);
        Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));
    }

    [Fact]
    public void Undrained_entries_accumulate_then_clear_on_drain()
    {
        var world = new World();
        var hp = world.Track<HP>();

        // Create 1 entity with HP — do NOT drain
        world.Create(new HP(10));

        // Create 2 more — still not drained
        world.Create(new HP(20));
        world.Create(new HP(30));

        // Drain → sees all 3 (accumulated)
        var ts = hp.Transitions().ToList();
        Assert.Equal(3, ts.Count);
        Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));

        // Create 1 more
        world.Create(new HP(40));

        // Drain → sees only 1 (previous 3 were cleared)
        ts = hp.Transitions().ToList();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
    }

    [Fact]
    public void Multiple_drain_cycles_stable()
    {
        var world = new World();
        var hp = world.Track<HP>();
        var totalSeen = 0;

        // Repeat 10 cycles: create 5, drain (auto-clears)
        for (var cycle = 0; cycle < 10; cycle++)
        {
            for (var j = 0; j < 5; j++)
                world.Create(new HP(cycle * 10 + j));

            var ts = hp.Transitions().ToList();
            Assert.Equal(5, ts.Count);
            Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));
            totalSeen += ts.Count;
        }

        Assert.Equal(50, totalSeen);
    }

    // ── Multi-cursor sharing ───────────────────────────────────────

    [Fact]
    public void Two_cursors_independent_progress()
    {
        var world = new World();
        var cursor1 = world.Track<HP>();
        var cursor2 = world.Track<HP>();

        // Both see the same first entity
        var e1 = world.Create(new HP(100));
        var ts1 = cursor1.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Equal(e1, ts1[0].Entity);

        var ts2 = cursor2.Transitions().ToList();
        Assert.Single(ts2);
        Assert.Equal(TransitionKind.Entered, ts2[0].Kind);
        Assert.Equal(e1, ts2[0].Entity);

        // Both see the same second entity
        var e2 = world.Create(new HP(50));
        ts1 = cursor1.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Equal(e2, ts1[0].Entity);

        ts2 = cursor2.Transitions().ToList();
        Assert.Single(ts2);
        Assert.Equal(TransitionKind.Entered, ts2[0].Kind);
        Assert.Equal(e2, ts2[0].Entity);

        // Both fully drained
        Assert.Empty(cursor1.Transitions());
        Assert.Empty(cursor2.Transitions());
    }

    [Fact]
    public void Two_cursors_staggered_consumption()
    {
        var world = new World();
        var cursor1 = world.Track<HP>();
        var cursor2 = world.Track<HP>();

        // Create 3 entities (3 Entered entries in log)
        world.Create(new HP(10));
        world.Create(new HP(20));
        world.Create(new HP(30));

        // cursor1 drains all 3
        var ts1 = cursor1.Transitions().ToList();
        Assert.Equal(3, ts1.Count);
        Assert.True(ts1.All(t => t.Kind == TransitionKind.Entered));

        // Create 1 more entity (1 more Entered)
        world.Create(new HP(40));

        // cursor2 still at position 0 → sees all 4
        var ts2 = cursor2.Transitions().ToList();
        Assert.Equal(4, ts2.Count);
        Assert.True(ts2.All(t => t.Kind == TransitionKind.Entered));

        // cursor1 only sees the new one
        ts1 = cursor1.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);

        // Both fully drained
        Assert.Empty(cursor1.Transitions());
        Assert.Empty(cursor2.Transitions());
    }

    [Fact]
    public void Two_cursors_different_filters()
    {
        var world = new World();
        var cursor1 = world.Track<HP>().Without<Dead>();
        var cursor2 = world.Track<HP>();

        // Create entity {HP} → both see Entered
        var e = world.Create(new HP(100));
        var ts1 = cursor1.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Equal(e, ts1[0].Entity);

        var ts2 = cursor2.Transitions().ToList();
        Assert.Single(ts2);
        Assert.Equal(TransitionKind.Entered, ts2[0].Kind);
        Assert.Equal(e, ts2[0].Entity);

        // Add Dead → cursor1 sees Exited (entity left {HP,!Dead}),
        // cursor2 sees nothing (HP still present)
        world.Add(e, new Dead());
        ts1 = cursor1.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Exited, ts1[0].Kind);

        ts2 = cursor2.Transitions().ToList();
        Assert.Empty(ts2);

        // Remove Dead → cursor1 sees Entered (re-entered {HP,!Dead}),
        // cursor2 sees nothing
        world.Remove<Dead>(e);
        ts1 = cursor1.Transitions().ToList();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);

        ts2 = cursor2.Transitions().ToList();
        Assert.Empty(ts2);
    }

    // ── TransitionCause ─────────────────────────────────────────────

    [Fact]
    public void Cause_is_Created_on_Create()
    {
        var world = new World();
        var hp = world.Track<HP>();

        world.Create(new HP(100));
        var t = hp.Transitions().Single();
        Assert.Equal(TransitionKind.Entered, t.Kind);
        Assert.Equal(TransitionCause.Created, t.Cause);
    }

    [Fact]
    public void Cause_is_Destroyed_on_Destroy()
    {
        var world = new World();
        var hp = world.Track<HP>();
        var e = world.Create(new HP(100));
        hp.Transitions(); // drain

        world.Destroy(e);
        var t = hp.Transitions().Single();
        Assert.Equal(TransitionKind.Exited, t.Kind);
        Assert.Equal(TransitionCause.Destroyed, t.Cause);
    }

    [Fact]
    public void Cause_is_Added_on_Add_matching_component()
    {
        var world = new World();
        var hp = world.Track<HP>().With<Enemy>();
        var e = world.Create(new HP(100)); // no Enemy yet
        hp.Transitions(); // drain

        world.Add(e, new Enemy()); // now {HP, Enemy} → matches filter
        var t = hp.Transitions().Single();
        Assert.Equal(TransitionKind.Entered, t.Kind);
        Assert.Equal(TransitionCause.Added, t.Cause);
    }

    [Fact]
    public void Cause_is_Removed_on_Remove_matching_component()
    {
        var world = new World();
        var hp = world.Track<HP>();
        var e = world.Create(new HP(100));
        hp.Transitions(); // drain

        world.Remove<HP>(e);
        var t = hp.Transitions().Single();
        Assert.Equal(TransitionKind.Exited, t.Kind);
        Assert.Equal(TransitionCause.Removed, t.Cause);
    }

    // ── WithPreviousValues ───────────────────────────────────────────

    [Fact]
    public void PreviousValues_captures_old_and_new()
    {
        var world = new World();
        var hp = world.Track<HP>().WithPreviousValues();
        var e = world.Create(new HP(100));

        world.Set(e, new HP(80));
        var cs = hp.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(e, cs[0].Entity);
        Assert.Equal(100, cs[0].OldValue.Value);  // created with 100
        Assert.Equal(80, cs[0].NewValue.Value);   // Set to 80

        world.Set(e, new HP(50));
        cs = hp.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(80, cs[0].OldValue.Value);   // previous Set's new = this Set's old
        Assert.Equal(50, cs[0].NewValue.Value);
    }

    [Fact]
    public void Without_WithPreviousValues_no_capture()
    {
        var world = new World();
        var hp = world.Track<HP>(); // no WithPreviousValues
        var e = world.Create(new HP(100));

        world.Set(e, new HP(80));
        Assert.Empty(hp.Changes());
    }

    [Fact]
    public void PreviousValues_auto_clears()
    {
        var world = new World();
        var hp = world.Track<HP>().WithPreviousValues();
        var e = world.Create(new HP(100));

        world.Set(e, new HP(80));
        Assert.Single(hp.Changes()); // drain + auto-clear
        Assert.Empty(hp.Changes());  // nothing new

        world.Set(e, new HP(50));
        Assert.Single(hp.Changes()); // only the new Set
    }

    [Fact]
    public void PreviousValues_multiple_sets_before_drain()
    {
        var world = new World();
        var hp = world.Track<HP>().WithPreviousValues();
        var e = world.Create(new HP(100));

        world.Set(e, new HP(90));
        world.Set(e, new HP(80));
        world.Set(e, new HP(70));

        var cs = hp.Changes().ToList();
        Assert.Equal(3, cs.Count);
        Assert.Equal(100, cs[0].OldValue.Value);
        Assert.Equal(90, cs[0].NewValue.Value);
        Assert.Equal(90, cs[1].OldValue.Value);
        Assert.Equal(80, cs[1].NewValue.Value);
        Assert.Equal(80, cs[2].OldValue.Value);
        Assert.Equal(70, cs[2].NewValue.Value);
    }

    [Fact]
    public void PreviousValues_with_Without_filter_respected()
    {
        var world = new World();
        var hp = world.Track<HP>().Without<Dead>().WithPreviousValues();
        var alive = world.Create(new HP(100));
        var dead = world.Create(new HP(100));
        world.Add(dead, new Dead());

        world.Set(alive, new HP(80));  // matches {HP, !Dead} → captured
        world.Set(dead, new HP(80));   // {HP, Dead} doesn't match → skipped

        var cs = hp.Changes().ToList();
        Assert.Single(cs);
        Assert.Equal(alive, cs[0].Entity);
    }
}
