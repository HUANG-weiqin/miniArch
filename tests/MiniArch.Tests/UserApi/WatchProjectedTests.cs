using System;
using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Tests for the projected <see cref="ChangeWatch{TComponent,TValue,THandler}"/> API.
/// </summary>
public class WatchProjectedTests
{
    // ── Test components ──────────────────────────────────────────────

    private readonly record struct Position(int X, int Y);
    private readonly record struct MultiField(int A, int B);
    private readonly record struct AliveTag;

    // ── Projected handlers ───────────────────────────────────────────

    private struct PositionXHandler : IChangeHandler<Position, int>
    {
        public int Changes;
        public int Checksum;
        public int LastOld;
        public int LastNew;

        public int Project(in Position component) => component.X;

        public void OnChange(World world, Entity entity, int oldValue, int newValue)
        {
            Changes++;
            Checksum = HashCode.Combine(Checksum, entity.Id, oldValue, newValue);
            LastOld = oldValue;
            LastNew = newValue;
        }
    }

    private struct PositionYHandler : IChangeHandler<Position, int>
    {
        public int Changes;
        public int LastOld;
        public int LastNew;

        public int Project(in Position component) => component.Y;

        public void OnChange(World world, Entity entity, int oldValue, int newValue)
        {
            Changes++;
            LastOld = oldValue;
            LastNew = newValue;
        }
    }

    private struct MultiFieldAHandler : IChangeHandler<MultiField, int>
    {
        public int Changes;

        public int Project(in MultiField component) => component.A;

        public void OnChange(World world, Entity entity, int oldValue, int newValue)
        {
            Changes++;
        }
    }

    // ── Snapshot → Diff: basic sequence ─────────────────────────

    [Fact]
    public void Snapshot_Diff_baseline_change_in_X_reported()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e = world.Create(new Position(10, 20));
        watch.Snapshot(world);

        world.Set(e, new Position(30, 40)); // X changed

        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes);
        Assert.Equal(10, watch.Handler.LastOld);
        Assert.Equal(30, watch.Handler.LastNew);
    }

    [Fact]
    public void Snapshot_Diff_only_projected_field_triggers()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e = world.Create(new Position(10, 20));
        watch.Snapshot(world);

        world.Set(e, new Position(10, 99)); // only Y changed (not projected)

        watch.Diff(world);
        Assert.Equal(0, watch.Handler.Changes);
    }

    [Fact]
    public void Snapshot_Diff_default_value_match_no_report()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        // X is already 0; setting default is no-op
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.Changes);
    }

    // ── Multiple Diffs ───────────────────────────────────────────

    [Fact]
    public void Multiple_Diffs_repeat_callbacks()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e = world.Create(new Position(10, 20));
        watch.Snapshot(world);
        world.Set(e, new Position(30, 40));

        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes);

        watch.Diff(world); // second Diff
        Assert.Equal(2, watch.Handler.Changes);
    }

    // ── Independent Watches ──────────────────────────────────────

    [Fact]
    public void Independent_Watches_isolated()
    {
        using var world = new World();
        var watchX = world.Watch<Position, int, PositionXHandler>();
        watchX.Handler = new PositionXHandler();
        var watchY = world.Watch<Position, int, PositionYHandler>();
        watchY.Handler = new PositionYHandler();

        var e = world.Create(new Position(10, 20));
        watchX.Snapshot(world);
        watchY.Snapshot(world);

        world.Set(e, new Position(30, 40)); // X and Y both change

        watchX.Diff(world);
        Assert.Equal(1, watchX.Handler.Changes);
        Assert.Equal(10, watchX.Handler.LastOld);
        Assert.Equal(30, watchX.Handler.LastNew);

        watchY.Diff(world);
        Assert.Equal(1, watchY.Handler.Changes);
        Assert.Equal(20, watchY.Handler.LastOld);
        Assert.Equal(40, watchY.Handler.LastNew);
    }

    // ── Explicit query ───────────────────────────────────────────

    [Fact]
    public void Explicit_query_filters_which_entities_match()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>(
            new QueryDescription().With<Position>());
        watch.Handler = new PositionXHandler();
        var e1 = world.Create(new Position(10, 20));
        world.Create(new Position(15, 25)); // not tracked in diff (but will be in snapshot)
        watch.Snapshot(world);

        world.Set(e1, new Position(30, 20));

        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes);
    }

    // ── Snapshot again resets baseline ───────────────────────────

    [Fact]
    public void Consecutive_Snapshot_resets_baseline()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e = world.Create(new Position(10, 20));
        watch.Snapshot(world);

        world.Set(e, new Position(30, 40));
        watch.Snapshot(world); // re-baseline at (30,40)

        watch.Diff(world);
        Assert.Equal(0, watch.Handler.Changes); // no diff vs new baseline

        world.Set(e, new Position(50, 60));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes);
        Assert.Equal(30, watch.Handler.LastOld);
        Assert.Equal(50, watch.Handler.LastNew);
    }

    // ── Null / disposed guards ───────────────────────────────────

    [Fact]
    public void Snapshot_null_world_throws()
    {
        var watch = new World().Watch<Position, int, PositionXHandler>();
        Assert.Throws<ArgumentNullException>(() => watch.Snapshot(null!));
    }

    [Fact]
    public void Diff_null_world_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Snapshot(world);
        Assert.Throws<ArgumentNullException>(() => watch.Diff(null!));
    }

    [Fact]
    public void Diff_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        Assert.Throws<InvalidOperationException>(() => watch.Diff(world));
    }

    [Fact]
    public void Diff_null_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        // ArgumentNullException takes priority over InvalidOperationException
        Assert.Throws<ArgumentNullException>(() => watch.Diff(null!));
    }

    [Fact]
    public void Disposed_world_throws()
    {
        var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Snapshot(world);
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => watch.Diff(world));
    }

    // ── Edge cases ───────────────────────────────────────────────

    [Fact]
    public void No_matching_entities_snapshot_then_diff_no_changes()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Snapshot(world);
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.Changes);
    }

    [Fact]
    public void Entity_added_after_snapshot_stale_old()
    {
        using var world = new World(entityCapacity: 1);
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e1 = world.Create(new Position(10, 20));
        watch.Snapshot(world);
        world.Destroy(e1);
        var e2 = world.Create(new Position(99, 99));
        watch.Diff(world);
        // Should report the change from old snapshot value (X=10) to new (X=99)
        Assert.Equal(1, watch.Handler.Changes);
    }

    [Fact]
    public void Entity_removed_after_snapshot_not_reported()
    {
        using var world = new World();
        var watch = world.Watch<Position, int, PositionXHandler>();
        watch.Handler = new PositionXHandler();
        var e = world.Create(new Position(10, 20));
        watch.Snapshot(world);
        world.Remove<Position>(e);
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.Changes);
    }
}
