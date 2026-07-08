using MiniArch;
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Tests for <see cref="SharedValueChanges{T}"/> API.
/// </summary>
public class ChangeQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);

    // ── TrackValueChanges<T> basic ─────────────────────────────────

    [Fact]
    public void TrackValueChanges_captures_direct_World_Set()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(10, 20), changes[0].New);
    }

    [Fact]
    public void TrackValueChanges_captures_CommandStream_Set()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        stream.Set(e, new Position(4, 5));
        Assert.True(stream.Submit());

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(4, 5), changes[0].New);
    }

    [Fact]
    public void TrackValueChanges_captures_GetRef_direct_write()
    {
        using var world = new World();
        var e = world.Create(new Position(0, 0));
        var positions = world.TrackValueChanges<Position>();

        ref var position = ref world.GetRef<Position>(e);
        position = new Position(7, 8);

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(7, 8), changes[0].New);
    }

    [Fact]
    public void TrackValueChanges_captures_chunk_span_direct_write()
    {
        using var world = new World();
        var e = world.Create(new Position(0, 0));
        var positions = world.TrackValueChanges<Position>();
        var query = world.Query(new QueryDescription().With<Position>());

        foreach (var chunk in query.GetChunks())
        {
            var span = chunk.GetSpan<Position>();
            span[0] = new Position(9, 10);
            break;
        }

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(9, 10), changes[0].New);
    }

    [Fact]
    public void TrackValueChanges_does_not_retroactively_capture_writes_before_arming()
    {
        using var world = new World();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        // Arm tracker after the write
        var positions = world.TrackValueChanges<Position>();

        // No changes from before arming
        Assert.Equal(0, positions.Changes.Length);

        // Subsequent writes are captured
        world.Set(e, new Position(30, 40));
        Assert.Equal(1, positions.Changes.Length);
        Assert.Equal(new Position(10, 20), positions.Changes[0].Old);
        Assert.Equal(new Position(30, 40), positions.Changes[0].New);
    }

    [Fact]
    public void TrackValueChanges_same_entity_multiple_sets_keeps_first_old_and_latest_new()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(1, 1));
        world.Set(e, new Position(2, 3));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(2, 3), changes[0].New);
    }

    [Fact]
    public void ClearAll_empties_value_changes()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, positions.Changes.Length);

        positions.ClearAll();

        Assert.Equal(0, positions.Changes.Length);
    }

    [Fact]
    public void After_ClearAll_new_set_produces_fresh_entry()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, positions.Changes.Length);
        positions.ClearAll();

        world.Set(e, new Position(30, 40));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(30, changes[0].New.X);
    }

    [Fact]
    public void Two_handles_same_type_alias_shared_log()
    {
        using var world = new World();
        var h1 = world.TrackValueChanges<Position>();
        var h2 = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        var span1 = h1.Changes;
        var span2 = h2.Changes;

        Assert.Equal(span1.Length, span2.Length);
        Assert.Equal(1, span1.Length);
        Assert.Equal(span1[0].Entity, span2[0].Entity);
        Assert.Equal(span1[0].New.X, span2[0].New.X);
    }

    [Fact]
    public void ClearAll_on_one_handle_affects_all_same_type()
    {
        using var world = new World();
        var h1 = world.TrackValueChanges<Position>();
        var h2 = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));
        world.Set(e, new Position(10, 20));

        Assert.Equal(1, h1.Changes.Length);
        Assert.Equal(1, h2.Changes.Length);

        h1.ClearAll();

        Assert.Equal(0, h1.Changes.Length);
        Assert.Equal(0, h2.Changes.Length);
    }

    [Fact]
    public void Different_types_remain_independent()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var velocities = world.TrackValueChanges<Velocity>();
        var e = world.Create(new Position(0, 0), new Velocity(1, 2));

        world.Set(e, new Position(10, 20));
        world.Set(e, new Velocity(30, 40));

        var posChanges = positions.Changes;
        Assert.Equal(1, posChanges.Length);
        Assert.Equal(new Position(0, 0), posChanges[0].Old);
        Assert.Equal(new Position(10, 20), posChanges[0].New);

        var velChanges = velocities.Changes;
        Assert.Equal(1, velChanges.Length);
        Assert.Equal(new Velocity(1, 2), velChanges[0].Old);
        Assert.Equal(new Velocity(30, 40), velChanges[0].New);
    }

    [Fact]
    public void Default_handle_is_safe()
    {
        var h = default(SharedValueChanges<Position>);
        Assert.Equal(0, h.Changes.Length);
        h.ClearAll(); // no-op, should not throw
    }

    [Fact]
    public void ClearAll_on_default_handle_is_noop()
    {
        var h = default(SharedValueChanges<Position>);
        h.ClearAll(); // should not throw
    }

    [Fact]
    public void Handle_throws_after_world_dispose()
    {
        var world = new World();
        var positions = world.TrackValueChanges<Position>();

        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = positions.Changes);
        Assert.Throws<ObjectDisposedException>(() => positions.ClearAll());
    }

    // ── RestoreState ──────────────────────────────────────────────

    [Fact]
    public void RestoreState_clears_value_changes()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(1, 2));

        var snap = world.CaptureState();

        world.Set(e, new Position(10, 20));
        Assert.Equal(1, positions.Changes.Length);

        world.RestoreState(snap);
        // After restore, SharedTrackers were cleared → no state
        Assert.Equal(0, positions.Changes.Length);
    }

    [Fact]
    public void RestoreState_preserves_handle_for_post_restore_mutations()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        var snap = world.CaptureState();
        world.Set(e, new Position(1, 1));
        world.RestoreState(snap);

        Assert.Equal(0, positions.Changes.Length);

        world.Set(e, new Position(2, 2));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(0, 0), changes[0].Old);
        Assert.Equal(new Position(2, 2), changes[0].New);
    }

    // ── World growth ─────────────────────────────────────────────

    [Fact]
    public void TrackValueChanges_handles_world_growth_after_arming()
    {
        using var world = new World(entityCapacity: 64);
        var positions = world.TrackValueChanges<Position>();

        Entity e1 = default;
        for (var i = 0; i < 65; i++)
            e1 = world.Create(new Position(i, i));

        Assert.True(world.EntityCapacity > 64);

        world.Set(e1, new Position(11, 11));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e1, changes[0].Entity);
        Assert.Equal(new Position(64, 64), changes[0].Old);
        Assert.Equal(new Position(11, 11), changes[0].New);
    }

    [Fact]
    public void Destroy_and_id_reuse_does_not_leak_stale_change()
    {
        using var world = new World(entityCapacity: 1);
        var positions = world.TrackValueChanges<Position>();
        var e1 = world.Create(new Position(0, 0));

        world.Set(e1, new Position(1, 1));
        Assert.Equal(1, positions.Changes.Length);
        positions.ClearAll();

        world.Destroy(e1);

        var e2 = world.Create(new Position(100, 100));
        Assert.Equal(e1.Id, e2.Id);
        Assert.NotEqual(e1, e2);

        world.Set(e2, new Position(101, 101));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e2, changes[0].Entity);
        Assert.Equal(new Position(100, 100), changes[0].Old);
        Assert.Equal(new Position(101, 101), changes[0].New);
    }

    // ── Net value diff semantics ─────────────────────────────────

    [Fact]
    public void TrackValueChanges_ignores_noop_set()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(5, 10));

        world.Set(e, new Position(5, 10)); // no-op: same value

        var changes = positions.Changes;
        Assert.Equal(0, changes.Length);
    }

    [Fact]
    public void TrackValueChanges_removes_change_when_value_returns_to_original()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20)); // A -> B
        world.Set(e, new Position(0, 0));   // B -> A (revert to original)

        var changes = positions.Changes;
        Assert.Equal(0, changes.Length);
    }

    [Fact]
    public void TrackValueChanges_revert_via_CommandStream()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        var stream = new CommandStream(world);
        stream.Set(e, new Position(10, 20));
        stream.Set(e, new Position(0, 0));
        Assert.True(stream.Submit());

        var changes = positions.Changes;
        Assert.Equal(0, changes.Length);
    }

    [Fact]
    public void TrackValueChanges_partial_revert_keeps_other_entities()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e1 = world.Create(new Position(0, 0));
        var e2 = world.Create(new Position(100, 100));

        world.Set(e1, new Position(10, 20)); // e1 changes: A -> B
        world.Set(e2, new Position(200, 200)); // e2 changes: A -> B
        world.Set(e1, new Position(0, 0));    // e1 reverts: B -> A (cancel)

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e2, changes[0].Entity); // only e2 remains
    }

    // ── Destroy/Remove with pending value change ──────────────

    [Fact]
    public void Destroy_with_pending_value_change_removes_change_entry()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20)); // creates a pending diff
        Assert.Equal(1, positions.Changes.Length);

        world.Destroy(e); // must remove the entry

        var changes = positions.Changes;
        Assert.Equal(0, changes.Length);
    }

    [Fact]
    public void Remove_component_with_pending_value_change_removes_change_entry()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20)); // creates a pending diff
        Assert.Equal(1, positions.Changes.Length);

        world.Remove<Position>(e); // must remove the entry

        var changes = positions.Changes;
        Assert.Equal(0, changes.Length);
    }

    [Fact]
    public void Remove_add_set_after_pending_dirty_does_not_duplicate()
    {
        using var world = new World();
        var positions = world.TrackValueChanges<Position>();
        var e = world.Create(new Position(0, 0));

        world.Set(e, new Position(10, 20)); // pending diff: (0,0) -> (10,20)
        world.Remove<Position>(e);          // must clean the entry
        world.Add(e, new Position(100, 100)); // Add does not track
        world.Set(e, new Position(200, 200)); // new diff: (100,100) -> (200,200)

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e, changes[0].Entity);
        Assert.Equal(new Position(100, 100), changes[0].Old);
        Assert.Equal(new Position(200, 200), changes[0].New);
    }

    [Fact]
    public void Destroy_then_id_reuse_with_pending_change_is_clean()
    {
        using var world = new World(entityCapacity: 1);
        var positions = world.TrackValueChanges<Position>();
        var e1 = world.Create(new Position(0, 0));

        world.Set(e1, new Position(10, 20)); // pending diff for e1
        world.Destroy(e1);                   // must remove the entry

        // Same id reused
        var e2 = world.Create(new Position(50, 50));
        Assert.Equal(e1.Id, e2.Id);
        world.Set(e2, new Position(60, 60));

        var changes = positions.Changes;
        Assert.Equal(1, changes.Length);
        Assert.Equal(e2, changes[0].Entity);
        Assert.Equal(new Position(50, 50), changes[0].Old);
        Assert.Equal(new Position(60, 60), changes[0].New);
    }
}
