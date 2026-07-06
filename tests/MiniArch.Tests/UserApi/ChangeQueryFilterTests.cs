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
}
