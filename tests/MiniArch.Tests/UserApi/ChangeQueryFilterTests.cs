using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Tests for <see cref="TransitionLog"/> API.
/// </summary>
public class ChangeQueryFilterTests
{
    // ── Test structs ────────────────────────────────────────────────
    private readonly record struct HP(int Value);
    private readonly record struct Dead;
    private readonly record struct Enemy;
    private readonly record struct Position(int X, int Y);

    private static MiniArch.TransitionLog Track(World world)
    {
        return world.TrackTransitions(new QueryDescription().With<HP>());
    }

    private static MiniArch.TransitionLog TrackFiltered(World world)
    {
        return world.TrackTransitions(new QueryDescription().With<HP>().Without<Dead>());
    }

    [Fact]
    public void TrackTransitions_rejects_empty_filter()
    {
        using var world = new World();

        var ex = Assert.Throws<ArgumentException>(() => world.TrackTransitions(new QueryDescription()));
        Assert.Contains("With<T>()", ex.Message);
    }

    [Fact]
    public void TrackTransitions_accepts_without_only_filter()
    {
        using var world = new World();
        var log = world.TrackTransitions(new QueryDescription().Without<Dead>());

        var e = world.Create(new HP(100));
        var ts = log.Transitions.ToArray();

        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    [Fact]
    public void TrackTransitions_accepts_withany_only_filter()
    {
        using var world = new World();
        var log = world.TrackTransitions(new QueryDescription().WithAny<HP>());

        var e = world.Create(new HP(100));
        var ts = log.Transitions.ToArray();

        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    // ── Without ────────────────────────────────────────────────────

    [Fact]
    public void Without_basic_transition()
    {
        using var world = new World();
        var log = TrackFiltered(world);

        var e = world.Create(new HP(100));
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
        log.Clear();

        world.Add(e, new Dead());
        ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
        log.Clear();

        world.Remove<Dead>(e);
        ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    // ── With ───────────────────────────────────────────────────────

    [Fact]
    public void With_filter_narrows_membership()
    {
        using var world = new World();
        var log = world.TrackTransitions(new QueryDescription().With<HP>().With<Enemy>());

        // Entity with HP but no Enemy → does not match filter
        var e = world.Create(new HP(100));
        var ts = log.Transitions.ToArray();
        Assert.Empty(ts);

        // Add Enemy → now matches
        world.Add(e, new Enemy());
        ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
        log.Clear();

        // Remove Enemy → no longer matches
        world.Remove<Enemy>(e);
        ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Exited, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
    }

    // ── Default filter backward compatibility ──────────────────────

    [Fact]
    public void Default_filter_backward_compatible()
    {
        using var world = new World();
        var log = Track(world);

        var e = world.Create(new HP(100));
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e, ts[0].Entity);
        log.Clear();

        // Adding Dead does not remove HP → entity still has HP → default filter still matches
        world.Add(e, new Dead());
        ts = log.Transitions.ToArray();
        Assert.Empty(ts);  // no transition: entity has HP before and after
    }

    // ── Multiple tracked types, independent filters ────────────────

    [Fact]
    public void Multiple_tracked_types_independent_filters()
    {
        using var world = new World();
        var filtered = TrackFiltered(world);
        var unfiltered = Track(world);

        var e = world.Create(new HP(100));
        var ts1 = filtered.Transitions.ToArray();
        var ts2 = unfiltered.Transitions.ToArray();
        Assert.Single(ts1);  // Entered in filtered (no Dead)
        Assert.Single(ts2);  // Entered in unfiltered (has HP)
        filtered.Clear();
        unfiltered.Clear();

        world.Add(e, new Dead());
        ts1 = filtered.Transitions.ToArray();
        ts2 = unfiltered.Transitions.ToArray();
        Assert.Single(ts1);  // Exited from filtered (now has Dead)
        Assert.Empty(ts2);   // Unfiltered: HP still present, no transition
    }

    // ── Chained filters ────────────────────────────────────────────

    [Fact]
    public void Chained_filters()
    {
        using var world = new World();
        // Track HP entities that also have Enemy but do NOT have Dead
        var log = world.TrackTransitions(new QueryDescription().With<HP>().With<Enemy>().Without<Dead>());

        // Entity {HP, Enemy} → matches filter
        var e1 = world.Create(new HP(100), new Enemy());
        var ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e1, ts[0].Entity);
        log.Clear();

        // Entity {HP, Enemy, Dead} → excluded by Without<Dead>
        var e2 = world.Create(new HP(50), new Enemy(), new Dead());
        ts = log.Transitions.ToArray();
        Assert.Empty(ts);

        // Entity {HP} alone → missing Enemy → does not match
        var e3 = world.Create(new HP(30));
        ts = log.Transitions.ToArray();
        Assert.Empty(ts);

        // Entity {HP, Enemy, Position} → matches (Position is irrelevant)
        var e4 = world.Create(new HP(20), new Enemy(), new Position(1, 2));
        ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
        Assert.Equal(e4, ts[0].Entity);
    }

    // ── Auto-clear ────────────────────────────────────────────

    [Fact]
    public void Transitions_auto_clears_after_drain()
    {
        using var world = new World();
        var log = Track(world);

        // Create 3 entities with HP → cursor sees 3 Entered
        world.Create(new HP(10));
        world.Create(new HP(20));
        world.Create(new HP(30));
        var ts = log.Transitions.ToArray();
        Assert.Equal(3, ts.Length);
        Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));

        // Transitions is non-destructive so we need to clear manually
        log.Clear();

        // Create 2 more entities with HP → cursor should see only 2 (not 5)
        world.Create(new HP(40));
        world.Create(new HP(50));
        ts = log.Transitions.ToArray();
        Assert.Equal(2, ts.Length);
        Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));
    }

    [Fact]
    public void Undrained_entries_accumulate_then_clear_on_drain()
    {
        using var world = new World();
        var log = Track(world);

        // Create 1 entity with HP — do NOT clear
        world.Create(new HP(10));

        // Create 2 more — still not cleared
        world.Create(new HP(20));
        world.Create(new HP(30));

        // Read → sees all 3 (accumulated)
        var ts = log.Transitions.ToArray();
        Assert.Equal(3, ts.Length);
        Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));

        log.Clear();

        // Create 1 more
        world.Create(new HP(40));

        // Read → sees only 1 (previous 3 were cleared)
        ts = log.Transitions.ToArray();
        Assert.Single(ts);
        Assert.Equal(TransitionKind.Entered, ts[0].Kind);
    }

    [Fact]
    public void Multiple_clear_cycles_stable()
    {
        using var world = new World();
        var log = Track(world);
        var totalSeen = 0;

        // Repeat 10 cycles: create 5, clear
        for (var cycle = 0; cycle < 10; cycle++)
        {
            for (var j = 0; j < 5; j++)
                world.Create(new HP(cycle * 10 + j));

            var ts = log.Transitions.ToArray();
            Assert.Equal(5, ts.Length);
            Assert.True(ts.All(t => t.Kind == TransitionKind.Entered));
            totalSeen += ts.Length;
            log.Clear();
        }

        Assert.Equal(50, totalSeen);
    }

    // ── Multi-cursor sharing ───────────────────────────────────────

    [Fact]
    public void Two_logs_independent_progress()
    {
        using var world = new World();
        var log1 = Track(world);
        var log2 = Track(world);

        // Both see the same first entity
        var e1 = world.Create(new HP(100));
        var ts1 = log1.Transitions.ToArray();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Equal(e1, ts1[0].Entity);

        var ts2 = log2.Transitions.ToArray();
        Assert.Single(ts2);
        Assert.Equal(TransitionKind.Entered, ts2[0].Kind);
        Assert.Equal(e1, ts2[0].Entity);

        log1.Clear();
        log2.Clear();

        // Both see the same second entity
        var e2 = world.Create(new HP(50));
        ts1 = log1.Transitions.ToArray();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Equal(e2, ts1[0].Entity);

        ts2 = log2.Transitions.ToArray();
        Assert.Single(ts2);
        Assert.Equal(TransitionKind.Entered, ts2[0].Kind);
        Assert.Equal(e2, ts2[0].Entity);

        // Both fully cleared
        log1.Clear();
        log2.Clear();
        Assert.Empty(log1.Transitions.ToArray());
        Assert.Empty(log2.Transitions.ToArray());
    }

    [Fact]
    public void Two_logs_staggered_consumption()
    {
        using var world = new World();
        var log1 = Track(world);
        var log2 = Track(world);

        // Create 3 entities (3 Entered entries in log)
        world.Create(new HP(10));
        world.Create(new HP(20));
        world.Create(new HP(30));

        // log1 reads all 3, then clears
        var ts1 = log1.Transitions.ToArray();
        Assert.Equal(3, ts1.Length);
        Assert.True(ts1.All(t => t.Kind == TransitionKind.Entered));
        log1.Clear();

        // Create 1 more entity (1 more Entered)
        world.Create(new HP(40));

        // log2 still at position 0 → sees all 4
        var ts2 = log2.Transitions.ToArray();
        Assert.Equal(4, ts2.Length);
        Assert.True(ts2.All(t => t.Kind == TransitionKind.Entered));

        // log1 only sees the new one
        ts1 = log1.Transitions.ToArray();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
    }

    [Fact]
    public void Two_logs_different_filters()
    {
        using var world = new World();
        var log1 = TrackFiltered(world);
        var log2 = Track(world);

        // Create entity {HP} → both see Entered
        var e = world.Create(new HP(100));
        var ts1 = log1.Transitions.ToArray();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);
        Assert.Equal(e, ts1[0].Entity);

        var ts2 = log2.Transitions.ToArray();
        Assert.Single(ts2);
        Assert.Equal(TransitionKind.Entered, ts2[0].Kind);
        Assert.Equal(e, ts2[0].Entity);

        log1.Clear();
        log2.Clear();

        // Add Dead → log1 sees Exited (entity left {HP,!Dead}),
        // log2 sees nothing (HP still present)
        world.Add(e, new Dead());
        ts1 = log1.Transitions.ToArray();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Exited, ts1[0].Kind);

        ts2 = log2.Transitions.ToArray();
        Assert.Empty(ts2);
        log1.Clear();

        // Remove Dead → log1 sees Entered (re-entered {HP,!Dead}),
        // log2 sees nothing
        world.Remove<Dead>(e);
        ts1 = log1.Transitions.ToArray();
        Assert.Single(ts1);
        Assert.Equal(TransitionKind.Entered, ts1[0].Kind);

        ts2 = log2.Transitions.ToArray();
        Assert.Empty(ts2);
    }

    // ── TransitionCause ─────────────────────────────────────────────

    [Fact]
    public void Cause_is_Created_on_Create()
    {
        using var world = new World();
        var log = Track(world);

        world.Create(new HP(100));
        var t = log.Transitions.ToArray().Single();
        Assert.Equal(TransitionKind.Entered, t.Kind);
        Assert.Equal(TransitionCause.Created, t.Cause);
    }

    [Fact]
    public void Cause_is_Destroyed_on_Destroy()
    {
        using var world = new World();
        var log = Track(world);
        var e = world.Create(new HP(100));
        log.Clear();

        world.Destroy(e);
        var t = log.Transitions.ToArray().Single();
        Assert.Equal(TransitionKind.Exited, t.Kind);
        Assert.Equal(TransitionCause.Destroyed, t.Cause);
    }

    [Fact]
    public void Cause_is_Added_on_Add_matching_component()
    {
        using var world = new World();
        var log = world.TrackTransitions(new QueryDescription().With<HP>().With<Enemy>());
        var e = world.Create(new HP(100)); // no Enemy yet
        log.Clear();

        world.Add(e, new Enemy()); // now {HP, Enemy} → matches filter
        var t = log.Transitions.ToArray().Single();
        Assert.Equal(TransitionKind.Entered, t.Kind);
        Assert.Equal(TransitionCause.Added, t.Cause);
    }

    [Fact]
    public void Cause_is_Removed_on_Remove_matching_component()
    {
        using var world = new World();
        var log = Track(world);
        var e = world.Create(new HP(100));
        log.Clear();

        world.Remove<HP>(e);
        var t = log.Transitions.ToArray().Single();
        Assert.Equal(TransitionKind.Exited, t.Kind);
        Assert.Equal(TransitionCause.Removed, t.Cause);
    }
}
