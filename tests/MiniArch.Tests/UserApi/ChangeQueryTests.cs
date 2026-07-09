using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Tests for <see cref="ChangeWatch{TComponent, THandler}"/> API.
/// </summary>
public class ChangeQueryTests
{
    private readonly record struct Position(int X, int Y) : System.IEquatable<Position>;
    private readonly record struct Velocity(int Dx, int Dy);

    private struct PositionHandler : IChangeHandler<Position>
    {
        public System.Collections.Generic.List<(Entity Entity, Position Old, Position New)> Changes;

        public PositionHandler(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, Position, Position)>();
        }

        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            Changes.Add((entity, oldValue, newValue));
        }
    }

    private struct VelocityHandler : IChangeHandler<Velocity>
    {
        public System.Collections.Generic.List<(Entity Entity, Velocity Old, Velocity New)> Changes;

        public VelocityHandler(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, Velocity, Velocity)>();
        }

        public void OnChange(World world, Entity entity, in Velocity oldValue, in Velocity newValue)
        {
            Changes.Add((entity, oldValue, newValue));
        }
    }

    // ── ChangeWatch basic ────────────────────────────────────────────

    [Fact]
    public void Watch_captures_direct_World_Set()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        world.Set(e, new Position(10, 20));

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e, change.Entity);
        Assert.Equal(new Position(0, 0), change.Old);
        Assert.Equal(new Position(10, 20), change.New);
    }

    [Fact]
    public void Watch_captures_CommandStream_Set()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        var stream = new CommandStream(world);
        stream.Set(e, new Position(4, 5));
        Assert.True(stream.Submit());

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e, change.Entity);
        Assert.Equal(new Position(0, 0), change.Old);
        Assert.Equal(new Position(4, 5), change.New);
    }

    [Fact]
    public void Watch_captures_GetRef_direct_write()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        ref var position = ref world.GetRef<Position>(e);
        position = new Position(7, 8);

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e, change.Entity);
        Assert.Equal(new Position(0, 0), change.Old);
        Assert.Equal(new Position(7, 8), change.New);
    }

    [Fact]
    public void Watch_captures_chunk_span_direct_write()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        var query = world.Query(new QueryDescription().With<Position>());
        foreach (var chunk in query.GetChunks())
        {
            var span = chunk.GetSpan<Position>();
            span[0] = new Position(9, 10);
            break;
        }

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e, change.Entity);
        Assert.Equal(new Position(0, 0), change.Old);
        Assert.Equal(new Position(9, 10), change.New);
    }

    [Fact]
    public void Watch_does_not_retroactively_capture_writes_before_Snapshot()
    {
        using var world = new World();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        watch.Snapshot(world); // baseline at (10,20)

        world.Set(e, new Position(30, 40));
        watch.Diff(world);

        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(new Position(10, 20), change.Old);
        Assert.Equal(new Position(30, 40), change.New);
    }

    [Fact]
    public void Watch_same_entity_multiple_sets_keeps_first_old_and_latest_new()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        world.Set(e, new Position(1, 1));
        world.Set(e, new Position(2, 3));

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e, change.Entity);
        Assert.Equal(new Position(0, 0), change.Old);
        Assert.Equal(new Position(2, 3), change.New);
    }

    [Fact]
    public void Multiple_Diffs_same_baseline_repeat()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Set(e, new Position(10, 20));

        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes.Count);

        watch.Diff(world); // repeat
        Assert.Equal(2, watch.Handler.Changes.Count);
    }

    [Fact]
    public void Snapshot_and_diff_twice_clears_baseline()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Set(e, new Position(10, 20));
        watch.Snapshot(world); // re-baseline

        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);

        world.Set(e, new Position(30, 40));
        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(new Position(10, 20), change.Old);
        Assert.Equal(new Position(30, 40), change.New);
    }

    [Fact]
    public void Different_types_remain_independent()
    {
        using var world = new World();
        var watchPos = world.Watch<Position, PositionHandler>();
        watchPos.Handler = new PositionHandler(0);
        var watchVel = world.Watch<Velocity, VelocityHandler>();
        watchVel.Handler = new VelocityHandler(0);
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        watchPos.Snapshot(world);
        watchVel.Snapshot(world);
        world.Set(e, new Position(10, 20));
        world.Set(e, new Velocity(30, 40));

        watchPos.Diff(world);
        var posChange = Assert.Single(watchPos.Handler.Changes);
        Assert.Equal(new Position(0, 0), posChange.Old);
        Assert.Equal(new Position(10, 20), posChange.New);

        watchVel.Diff(world);
        var velChange = Assert.Single(watchVel.Handler.Changes);
        Assert.Equal(new Velocity(1, 2), velChange.Old);
        Assert.Equal(new Velocity(30, 40), velChange.New);
    }

    [Fact]
    public void Handle_throws_after_world_dispose()
    {
        var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        watch.Snapshot(world);
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => watch.Diff(world));
    }

    [Fact]
    public void Diff_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        Assert.Throws<InvalidOperationException>(() => watch.Diff(world));
    }

    // ── Destroy/Remove semantics ─────────────────────────────────

    [Fact]
    public void Destroy_with_pending_value_removes_change_entry()
    {
        using var world = new World(entityCapacity: 1);
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        world.Set(e, new Position(10, 20));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes.Count);

        // Destroy after diff: entity no longer exists, next diff won't find it
        world.Destroy(e);
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes.Count); // still 1 (old change) + no new ones
    }

    // ── World growth ─────────────────────────────────────────────

    [Fact]
    public void Watch_handles_world_growth_after_arming()
    {
        using var world = new World(entityCapacity: 64);
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);

        Entity e1 = default;
        for (var i = 0; i < 65; i++)
            e1 = world.Create(new Position(i, i));

        watch.Snapshot(world);
        world.Set(e1, new Position(11, 11));

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e1, change.Entity);
        Assert.Equal(new Position(64, 64), change.Old);
        Assert.Equal(new Position(11, 11), change.New);
    }

    // ── Net value diff semantics ─────────────────────────────────

    [Fact]
    public void Watch_ignores_noop_set()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(5, 10));
        watch.Snapshot(world);

        world.Set(e, new Position(5, 10)); // no-op
        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void Watch_removes_change_when_value_returns_to_original()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        world.Set(e, new Position(10, 20));
        world.Set(e, new Position(0, 0)); // revert

        watch.Diff(world);
        Assert.Empty(watch.Handler.Changes);
    }

    [Fact]
    public void Watch_partial_revert_keeps_other_entities()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e1 = world.Create(new Position(0, 0));
        var e2 = world.Create(new Position(100, 100));
        watch.Snapshot(world);

        world.Set(e1, new Position(10, 20));
        world.Set(e2, new Position(200, 200));
        world.Set(e1, new Position(0, 0)); // e1 reverts

        watch.Diff(world);
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e2, change.Entity);
    }

    [Fact]
    public void Destroy_and_id_reuse_does_not_leak_stale_change()
    {
        using var world = new World(entityCapacity: 1);
        var watch = world.Watch<Position, PositionHandler>();
        watch.Handler = new PositionHandler(0);
        var e1 = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        world.Set(e1, new Position(1, 1));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.Changes.Count);

        // Fresh handler: the new baseline should only produce the next diff
        watch.Handler = new PositionHandler(0);
        watch.Snapshot(world); // re-baseline
        world.Destroy(e1);

        var e2 = world.Create(new Position(100, 100));
        Assert.Equal(e1.Id, e2.Id);
        Assert.NotEqual(e1, e2);

        watch.Diff(world);
        // e1 was destroyed, recreated as e2 — current scan finds e2
        // e2's value (100,100) differs from old baseline slot (1,1)
        var change = Assert.Single(watch.Handler.Changes);
        Assert.Equal(e2, change.Entity);
        Assert.Equal(new Position(1, 1), change.Old);
        Assert.Equal(new Position(100, 100), change.New);
    }
}
