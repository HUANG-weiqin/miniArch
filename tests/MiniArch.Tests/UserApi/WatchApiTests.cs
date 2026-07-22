using MiniArch;
using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Core semantics tests for the new pull-based Watch API (ChangeWatch / TransitionWatch).
/// </summary>
public class WatchApiTests
{
    private readonly record struct Position(int X, int Y) : System.IEquatable<Position>;
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Value) : System.IEquatable<Health>;
    private readonly record struct AliveTag;

    // ── Handler implementations for ChangeWatch<Position> ─────────────

    private struct PositionChangeHandler : IChangeHandler<Position>
    {
        public int CallCount;
        public Entity LastEntity;
        public Position LastOld;
        public Position LastNew;
        public System.Collections.Generic.List<(Entity, Position, Position)> AllChanges;

        public PositionChangeHandler(int _) // dummy discriminator
        {
            CallCount = 0;
            LastEntity = default;
            LastOld = default;
            LastNew = default;
            AllChanges = new System.Collections.Generic.List<(Entity, Position, Position)>();
        }

        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            CallCount++;
            LastEntity = entity;
            LastOld = oldValue;
            LastNew = newValue;
            AllChanges?.Add((entity, oldValue, newValue));
        }
    }

    private struct ProjectedXHandler : IChangeHandler<Position, int>
    {
        public int LastOld;
        public int LastNew;
        public int CallCount;

        public int Project(in Position component) => component.X;

        public void OnChange(World world, Entity entity, int oldValue, int newValue)
        {
            CallCount++;
            LastOld = oldValue;
            LastNew = newValue;
        }
    }

    // ── Handler for TransitionWatch ──────────────────────────────────

    private struct TransitionRecorder : ITransitionHandler
    {
        public int CallCount;
        public System.Collections.Generic.List<(Entity, TransitionKind)> AllChanges;

        public TransitionRecorder(int _)
        {
            CallCount = 0;
            AllChanges = new System.Collections.Generic.List<(Entity, TransitionKind)>();
        }

        public void OnChange(World world, Entity entity, TransitionKind kind)
        {
            CallCount++;
            AllChanges?.Add((entity, kind));
        }
    }

    private sealed class ReentrantChangeState
    {
        public ChangeWatch<Position, ReentrantChangeHandler>? Watch;
        public bool Reenter = true;
        public int CallCount;
    }

    private readonly struct ReentrantChangeHandler(ReentrantChangeState state) : IChangeHandler<Position>
    {
        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            state.CallCount++;
            if (!state.Reenter)
                return;

            state.Reenter = false;
            state.Watch!.Diff(world);
        }
    }

    private sealed class ReentrantTransitionState
    {
        public TransitionWatch<ReentrantTransitionHandler>? Watch;
        public bool Reenter = true;
        public int CallCount;
    }

    private readonly struct ReentrantTransitionHandler(ReentrantTransitionState state) : ITransitionHandler
    {
        public void OnChange(World world, Entity entity, TransitionKind kind)
        {
            state.CallCount++;
            if (!state.Reenter)
                return;

            state.Reenter = false;
            state.Watch!.Diff(world);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  ChangeWatch tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ChangeWatch_Snapshot_no_changes_Diff_empty()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        watch.Snapshot(world);
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.CallCount);
    }

    [Fact]
    public void ChangeWatch_Snapshot_Set_Diff_reports_change()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Set(e, new Position(10, 20));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(e, watch.Handler.LastEntity);
        Assert.Equal(new Position(0, 0), watch.Handler.LastOld);
        Assert.Equal(new Position(10, 20), watch.Handler.LastNew);
    }

    [Fact]
    public void ChangeWatch_consecutive_Snapshot_clears_baseline()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Set(e, new Position(10, 20));
        watch.Snapshot(world); // re-baseline: now (10,20) is the baseline
        world.Set(e, new Position(30, 40));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(new Position(10, 20), watch.Handler.LastOld);
        Assert.Equal(new Position(30, 40), watch.Handler.LastNew);
    }

    [Fact]
    public void ChangeWatch_multiple_Diff_repeats_same_baseline()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Set(e, new Position(10, 20));

        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);

        watch.Diff(world); // second Diff against same baseline
        Assert.Equal(2, watch.Handler.CallCount);
    }

    [Fact]
    public void BUG_ChangeWatch_reentrant_Diff_throws_and_recovers()
    {
        using var world = new World();
        var state = new ReentrantChangeState();
        var watch = world.Watch<Position, ReentrantChangeHandler>();
        watch.Handler = new ReentrantChangeHandler(state);
        state.Watch = watch;
        var entity = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Set(entity, new Position(1, 1));

        Assert.Throws<InvalidOperationException>(() => watch.Diff(world));

        watch.Diff(world);
        Assert.Equal(2, state.CallCount);
    }

    [Fact]
    public void ChangeWatch_destroy_recreate_reports_stale_old()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        // Destroy entity, add new entity at same id with a different value
        world.Destroy(e);
        var e2 = world.Create(new Position(100, 100));
        watch.Diff(world);
        // The old slot will show default(0,0) vs current (100,100)
        // because Snapshot captured the old entity's value
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(e2, watch.Handler.LastEntity);
    }

    [Fact]
    public void ChangeWatch_remove_after_Snapshot_not_reported()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Remove<Position>(e); // entity no longer matches query
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.CallCount);
    }

    [Fact]
    public void ChangeWatch_destroy_after_Snapshot_not_reported()
    {
        using var world = new World();
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Destroy(e);
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.CallCount);
    }

    [Fact]
    public void ChangeWatch_destroy_recreate_stale_slot_reported()
    {
        using var world = new World(entityCapacity: 1);
        var handler = new PositionChangeHandler(0);
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Handler = handler;
        var e1 = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Destroy(e1);
        var e2 = world.Create(new Position(100, 100));
        watch.Diff(world);
        // The old slot (id 0) has old value=(0,0) from e1, current value=(100,100) from e2
        // The reported entity should be e2 (current scan finds it)
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(e2, watch.Handler.LastEntity);
    }

    [Fact]
    public void ChangeWatch_Diff_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionChangeHandler>();
        var ex = Assert.Throws<InvalidOperationException>((Action)(() => watch.Diff(world)));
    }

    [Fact]
    public void ChangeWatch_Snapshot_null_world_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionChangeHandler>();
        // Cannot Snapshot with null world — ArgumentNullException
        Assert.Throws<ArgumentNullException>((Action)(() => watch.Snapshot(null!)));
    }

    [Fact]
    public void ChangeWatch_Diff_null_world_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionChangeHandler>();
        watch.Snapshot(world);
        Assert.Throws<ArgumentNullException>((Action)(() => watch.Diff(null!)));
    }

    [Fact]
    public void ChangeWatch_Diff_null_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<Position, PositionChangeHandler>();
        // ArgumentNullException takes priority over InvalidOperationException
        Assert.Throws<ArgumentNullException>((Action)(() => watch.Diff(null!)));
    }

    [Fact]
    public void ChangeWatch_Disposed_world_throws()
    {
        var world = new World();
        var watch = world.Watch<Position, PositionChangeHandler>();
        world.Dispose();
        Assert.Throws<ObjectDisposedException>((Action)(() => watch.Snapshot(world)));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Projected ChangeWatch tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ProjectedWatch_only_reports_on_projected_field_change()
    {
        using var world = new World();
        var handler = new ProjectedXHandler();
        var watch = world.Watch<Position, int, ProjectedXHandler>();
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);

        // Change Y (not projected) — should NOT trigger
        world.Set(e, new Position(0, 100));
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.CallCount);

        // Change X (projected) — should trigger
        world.Set(e, new Position(50, 100));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(0, watch.Handler.LastOld);
        Assert.Equal(50, watch.Handler.LastNew);
    }

    [Fact]
    public void ProjectedWatch_independent_watches_isolated()
    {
        using var world = new World();
        var h1 = new ProjectedXHandler();
        var h2 = new ProjectedXHandler();
        var watch1 = world.Watch<Position, int, ProjectedXHandler>();
        var watch2 = world.Watch<Position, int, ProjectedXHandler>();
        watch1.Handler = h1;
        watch2.Handler = h2;
        var e = world.Create(new Position(0, 0));
        watch1.Snapshot(world);
        watch2.Snapshot(world);

        world.Set(e, new Position(10, 20));

        watch1.Diff(world);
        Assert.Equal(1, watch1.Handler.CallCount);

        // watch2 hasn't Diff'd yet — handler should be at 0
        Assert.Equal(0, watch2.Handler.CallCount);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TransitionWatch tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TransitionWatch_no_changes_Diff_empty()
    {
        using var world = new World();
        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;
        watch.Snapshot(world);
        watch.Diff(world);
        Assert.Equal(0, watch.Handler.CallCount);
    }

    [Fact]
    public void TransitionWatch_create_entity_with_filter_entered()
    {
        using var world = new World();
        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;
        watch.Snapshot(world);
        var e = world.Create(new Position(0, 0));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(e, watch.Handler.AllChanges[0].Item1);
        Assert.Equal(TransitionKind.Entered, watch.Handler.AllChanges[0].Item2);
    }

    [Fact]
    public void TransitionWatch_destroy_entity_exited()
    {
        using var world = new World();
        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Destroy(e);
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(e, watch.Handler.AllChanges[0].Item1);
        Assert.Equal(TransitionKind.Exited, watch.Handler.AllChanges[0].Item2);
    }

    [Fact]
    public void TransitionWatch_add_component_entered()
    {
        using var world = new World();
        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;
        var e = world.CreateEmpty(); // empty, no Position
        watch.Snapshot(world);
        world.Add(e, new Position(0, 0));
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(TransitionKind.Entered, watch.Handler.AllChanges[0].Item2);
    }

    [Fact]
    public void TransitionWatch_remove_component_exited()
    {
        using var world = new World();
        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;
        var e = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Remove<Position>(e);
        watch.Diff(world);
        Assert.Equal(1, watch.Handler.CallCount);
        Assert.Equal(TransitionKind.Exited, watch.Handler.AllChanges[0].Item2);
    }

    [Fact]
    public void TransitionWatch_net_entered_for_snapshot_exit_reenter()
    {
        using var world = new World(entityCapacity: 1);
        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;
        var e1 = world.Create(new Position(0, 0));
        watch.Snapshot(world);
        world.Destroy(e1);
        var e2 = world.Create(new Position(100, 100));
        watch.Diff(world);
        // Same id in both snapshot (e1) and current (e2): id-based net semantics
        // yields no transition, per plan: "do not over-engineer version-aware sets"
        Assert.Equal(0, watch.Handler.CallCount);
    }

    [Fact]
    public void BUG_TransitionWatch_reentrant_Diff_throws_and_recovers()
    {
        using var world = new World();
        var state = new ReentrantTransitionState();
        var watch = world.Watch<ReentrantTransitionHandler>(new QueryDescription().With<Position>());
        watch.Handler = new ReentrantTransitionHandler(state);
        state.Watch = watch;
        watch.Snapshot(world);
        world.Create(new Position(0, 0));

        Assert.Throws<InvalidOperationException>(() => watch.Diff(world));

        watch.Diff(world);
        Assert.Equal(2, state.CallCount);
    }

    [Fact]
    public void TransitionWatch_Diff_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        var ex = Assert.Throws<InvalidOperationException>((Action)(() => watch.Diff(world)));
    }

    [Fact]
    public void TransitionWatch_Diff_null_before_Snapshot_throws()
    {
        using var world = new World();
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        // ArgumentNullException takes priority over InvalidOperationException
        Assert.Throws<ArgumentNullException>((Action)(() => watch.Diff(null!)));
    }

    // QueryDescription is a value type, so null is not a valid argument.
    // Empty filter is tested below.

    [Fact]
    public void TransitionWatch_empty_filter_throws()
    {
        using var world = new World();
        Assert.Throws<ArgumentException>((Action)(() => world.Watch<TransitionRecorder>(new QueryDescription())));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Allocation test: after warmup, steady-state alloc = 0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TransitionWatch_allocates_zero_per_iteration_after_warmup()
    {
        // Warm up the watch so internal arrays are stable-sized, then measure
        // a tight Snapshot+Diff loop for zero allocation.
        const int entityCount = 100;
        const int warmup = 50;
        const int iterations = 100;

        using var world = new World();
        for (var i = 0; i < entityCount; i++)
            world.Create(new Position(0, 0));

        var handler = new TransitionRecorder(0);
        var watch = world.Watch<TransitionRecorder>(new QueryDescription().With<Position>());
        watch.Handler = handler;

        // Warmup — stabilize bitset and entity array sizes
        for (var w = 0; w < warmup; w++)
        {
            watch.Snapshot(world);
            watch.Diff(world);
        }

        // Force full GC to clean up any prior noise before measurement
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            watch.Snapshot(world);
            watch.Diff(world);
        }

        var afterBytes = GC.GetAllocatedBytesForCurrentThread();
        var allocated = afterBytes - beforeBytes;

        // Allow tiny noise (e.g. xUnit framework accounting), but 0 is expected.
        // If this becomes flaky, bump threshold or remove — steady-state alloc
        // is already verified by WatchApi.Perf tools.
        Assert.True(allocated <= 32,
            $"Expected ≤32 B allocated after warmup, got {allocated} B over {iterations} iterations");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mutation safety: OnChange can mutate world without corruption
    // ═══════════════════════════════════════════════════════════════

    private struct DestructiveHandler : IChangeHandler<Position>
    {
        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            // Destroy the entity reporting the change — must not corrupt Diff iteration
            world.Destroy(entity);
        }
    }

    [Fact]
    public void ChangeWatch_OnChange_can_destroy_entity_without_corrupting_iteration()
    {
        using var world = new World();
        var watch = world.Watch<Position, DestructiveHandler>();
        watch.Handler = default;
        var e1 = world.Create(new Position(0, 0));
        var e2 = world.Create(new Position(1, 1));
        watch.Snapshot(world);
        world.Set(e1, new Position(10, 10));
        world.Set(e2, new Position(20, 20));
        // Should not throw
        watch.Diff(world);
    }

    private struct AddingHandler : IChangeHandler<Position>
    {
        public void OnChange(World world, Entity entity, in Position oldValue, in Position newValue)
        {
            // Add a component to the entity — changes archetype but must not corrupt
            world.Add(entity, new Velocity(1, 1));
        }
    }

    [Fact]
    public void ChangeWatch_OnChange_can_add_component_without_corrupting_iteration()
    {
        using var world = new World();
        var watch = world.Watch<Position, AddingHandler>();
        watch.Handler = default;
        var e1 = world.Create(new Position(0, 0));
        var e2 = world.Create(new Position(1, 1));
        watch.Snapshot(world);
        world.Set(e1, new Position(10, 10));
        world.Set(e2, new Position(20, 20));
        // Should not throw
        watch.Diff(world);
    }
}
