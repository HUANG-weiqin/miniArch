using MiniArch.Core;
using Xunit;

namespace MiniArchTests.UserApi;

/// <summary>
/// Semantics tests for <see cref="World.CreateDenseValueDiff{TComponent,TValue,TProjector}"/>.
/// </summary>
public class ExplicitDenseValueDiffTests
{
    // ── Test components ──────────────────────────────────────────────

    private readonly record struct Position(int X, int Y);
    private readonly record struct MultiField(int A, int B);
    private readonly record struct AliveTag;

    // ── Test projectors ──────────────────────────────────────────────

    private readonly struct PositionXProjector : IValueProjector<Position, int>
    {
        public int Project(in Position component) => component.X;
    }

    private readonly struct PositionYProjector : IValueProjector<Position, int>
    {
        public int Project(in Position component) => component.Y;
    }

    private readonly struct MultiFieldAProjector : IValueProjector<MultiField, int>
    {
        public int Project(in MultiField component) => component.A;
    }

    // ── Struct sinks ─────────────────────────────────────────────────

    internal struct CountSink : IValueChangeSink<int>
    {
        public int Count;
        public int Checksum;

        public void OnChanged(Entity entity, int oldValue, int newValue)
        {
            Count++;
            Checksum = HashCode.Combine(Checksum, entity.Id, oldValue, newValue);
        }
    }

    internal struct CaptureSink : IValueChangeSink<int>
    {
        public int TotalChanges;
        public System.Collections.Generic.List<(Entity Entity, int Old, int New)> Changes;

        public CaptureSink(int _)
        {
            Changes = new System.Collections.Generic.List<(Entity, int, int)>();
        }

        public void OnChanged(Entity entity, int oldValue, int newValue)
        {
            TotalChanges++;
            Changes.Add((entity, oldValue, newValue));
        }
    }

    // ── Tests ────────────────────────────────────────────────────────

    [Fact]
    public void Capture_Drain_reports_old_new()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        var e2 = world.Create(new Position(3, 4));

        diff.Capture(world);

        world.Set(e1, new Position(10, 20));
        world.Set(e2, new Position(30, 40));

        var sink = new CaptureSink(0);
        diff.Drain(world, ref sink);

        Assert.Equal(2, sink.TotalChanges);
        Assert.Contains((e1, 1, 10), sink.Changes);
        Assert.Contains((e2, 3, 30), sink.Changes);
    }

    [Fact]
    public void Capture_then_drain_without_changes_reports_nothing()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(5, 10));
        diff.Capture(world);

        var sink = new CountSink();
        diff.Drain(world, ref sink);

        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Second_drain_without_capture_returns_same_data()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        diff.Capture(world);
        world.Set(e1, new Position(10, 20));

        var sink1 = new CountSink();
        diff.Drain(world, ref sink1);
        Assert.Equal(1, sink1.Count);

        // Second drain without intermediate Capture — same data still reported
        var sink2 = new CountSink();
        diff.Drain(world, ref sink2);
        Assert.Equal(1, sink2.Count);
    }

    [Fact]
    public void Clear_resets_old_values()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        diff.Capture(world);
        world.Set(e1, new Position(10, 20));

        var sink1 = new CountSink();
        diff.Drain(world, ref sink1);
        Assert.Equal(1, sink1.Count);

        diff.Clear();

        // After Clear + new Capture + new Set, changes should use new baseline
        diff.Capture(world);
        world.Set(e1, new Position(100, 200));

        var sink2 = new CountSink();
        diff.Drain(world, ref sink2);
        Assert.Equal(1, sink2.Count);
    }

    [Fact]
    public void Add_entity_after_capture_uses_dense_shadow_semantics()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        // Create entity and capture its value
        var e1 = world.Create(new Position(1, 2));
        diff.Capture(world);

        // New entity created after Capture — its old value is default(0)
        var e2 = world.Create(new Position(42, 0));

        world.Set(e1, new Position(10, 20));
        // e2 stays at (42, 0) — old=default(0) → new=42 should be reported

        var sink = new CaptureSink(0);
        diff.Drain(world, ref sink);

        // e1 changed: 1→10
        Assert.Contains((e1, 1, 10), sink.Changes);
        // e2: old=default(0) → new=42 (dense shadow semantics)
        Assert.Contains((e2, 0, 42), sink.Changes);
    }

    [Fact]
    public void Remove_entity_after_capture_not_reported_in_drain()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        var e2 = world.Create(new Position(3, 4));

        diff.Capture(world);

        // Remove e2's Position component — entity is no longer in query
        world.Remove<Position>(e2);
        world.Set(e1, new Position(10, 20));

        var sink = new CountSink();
        diff.Drain(world, ref sink);

        // Only e1 should be reported, e2 was removed and is not scanned
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void Destroy_entity_after_capture_not_reported()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        var e2 = world.Create(new Position(3, 4));

        diff.Capture(world);

        world.Destroy(e2);
        world.Set(e1, new Position(10, 20));

        var sink = new CountSink();
        diff.Drain(world, ref sink);

        // Only e1 should be reported, e2 was destroyed and is not scanned
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void Projector_only_projects_selected_field()
    {
        using var world = new World();
        // Project only field A of MultiField
        var diff = world.CreateDenseValueDiff<MultiField, int, MultiFieldAProjector>();

        var e1 = world.Create(new MultiField(10, 20));
        diff.Capture(world);

        // Only field A changed — should report
        world.Set(e1, new MultiField(99, 20));

        var sink1 = new CountSink();
        diff.Drain(world, ref sink1);
        Assert.Equal(1, sink1.Count);

        diff.Clear();
        diff.Capture(world);

        // Only field B changed — should NOT report (projected A didn't change)
        world.Set(e1, new MultiField(99, 50));

        var sink2 = new CountSink();
        diff.Drain(world, ref sink2);
        Assert.Equal(0, sink2.Count);
    }

    [Fact]
    public void Multiple_instances_independent()
    {
        using var world = new World();
        var diffX = world.CreateDenseValueDiff<Position, int, PositionXProjector>();
        var diffY = world.CreateDenseValueDiff<Position, int, PositionYProjector>();

        var e1 = world.Create(new Position(1, 2));
        var e2 = world.Create(new Position(3, 4));

        diffX.Capture(world);
        diffY.Capture(world);

        world.Set(e1, new Position(10, 20));
        world.Set(e2, new Position(30, 40));

        // Drain diffX — should report X changes: 1→10, 3→30
        var sinkX = new CaptureSink(0);
        diffX.Drain(world, ref sinkX);
        Assert.Equal(2, sinkX.TotalChanges);
        Assert.Contains((e1, 1, 10), sinkX.Changes);
        Assert.Contains((e2, 3, 30), sinkX.Changes);

        // Drain diffY independently — should report Y changes: 2→20, 4→40
        var sinkY = new CaptureSink(0);
        diffY.Drain(world, ref sinkY);
        Assert.Equal(2, sinkY.TotalChanges);
        Assert.Contains((e1, 2, 20), sinkY.Changes);
        Assert.Contains((e2, 4, 40), sinkY.Changes);
    }

    [Fact]
    public void Sink_struct_is_called_per_change()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        var e2 = world.Create(new Position(3, 4));
        var e3 = world.Create(new Position(5, 6));

        diff.Capture(world);

        world.Set(e1, new Position(10, 20));
        world.Set(e2, new Position(30, 40));
        // e3 unchanged

        var sink = new CountSink();
        diff.Drain(world, ref sink);

        Assert.Equal(2, sink.Count);
    }

    [Fact]
    public void Query_filter_works()
    {
        using var world = new World();
        // Query that requires both Position and AliveTag
        var query = new QueryDescription().With<Position>().With<AliveTag>();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>(query);

        var e1 = world.Create(new Position(1, 2), new AliveTag());
        var e2 = world.Create(new Position(3, 4)); // no AliveTag — excluded

        diff.Capture(world);
        world.Set(e1, new Position(10, 20));
        world.Set(e2, new Position(30, 40));

        var sink = new CountSink();
        diff.Drain(world, ref sink);

        // Only e1 should be reported because e2 lacks AliveTag
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void Drain_without_capture_reports_nothing()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));

        // No Capture before Drain — should return empty
        var sink = new CountSink();
        diff.Drain(world, ref sink);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void Default_factory_query_includes_TComponent()
    {
        using var world = new World();
        // Using default query (no query parameter) should auto-With<Position>
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(1, 2));
        diff.Capture(world);

        world.Set(e1, new Position(10, 20));

        var sink = new CountSink();
        diff.Drain(world, ref sink);
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public void Capture_Drain_reports_correct_old_new_values()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(5, 0));
        diff.Capture(world);

        world.Set(e1, new Position(42, 0));

        var sink = new CaptureSink(0);
        diff.Drain(world, ref sink);

        Assert.Equal(1, sink.TotalChanges);
        Assert.Equal(e1, sink.Changes[0].Entity);
        Assert.Equal(5, sink.Changes[0].Old);
        Assert.Equal(42, sink.Changes[0].New);
    }

    // ── Null guard tests ─────────────────────────────────────────────────

    [Fact]
    public void Capture_null_world_throws_argument_null_exception()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();
        Assert.Throws<ArgumentNullException>(() => diff.Capture(null!));
    }

    [Fact]
    public void Drain_null_world_throws_argument_null_exception()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();
        var sink = new CountSink();
        Assert.Throws<ArgumentNullException>(() => diff.Drain(null!, ref sink));
    }

    // ── Dispose guard tests ───────────────────────────────────────────────

    [Fact]
    public void Capture_after_world_dispose_throws_object_disposed_exception()
    {
        var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => diff.Capture(world));
    }

    [Fact]
    public void Drain_after_world_dispose_throws_object_disposed_exception()
    {
        var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();
        diff.Capture(world);
        world.Dispose();
        var sink = new CountSink();
        Assert.Throws<ObjectDisposedException>(() => diff.Drain(world, ref sink));
    }

    // ── Intentional stale slot semantics ──

    [Fact]
    public void Consecutive_capture_clears_previous_dense_slots_for_entities_that_left_query()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(7, 0));
        diff.Capture(world);

        world.Destroy(e1);
        diff.Capture(world); // new baseline has no matching entities

        var e2 = world.Create(new Position(42, 0));
        Assert.Equal(e1.Id, e2.Id);

        var sink = new CaptureSink(0);
        diff.Drain(world, ref sink);

        Assert.Equal(1, sink.TotalChanges);
        Assert.Equal(e2, sink.Changes[0].Entity);
        Assert.Equal(0, sink.Changes[0].Old);
        Assert.Equal(42, sink.Changes[0].New);
    }

    [Fact]
    public void Destroy_then_recreate_same_id_after_capture_reports_stale_dense_slot_value()
    {
        using var world = new World();
        var diff = world.CreateDenseValueDiff<Position, int, PositionXProjector>();

        var e1 = world.Create(new Position(7, 0));
        diff.Capture(world);

        world.Destroy(e1);
        var e2 = world.Create(new Position(42, 0));
        // LIFO reuse: e2.Id should equal e1.Id
        Assert.Equal(e1.Id, e2.Id);

        var sink = new CaptureSink(0);
        diff.Drain(world, ref sink);

        Assert.Equal(1, sink.TotalChanges);
        Assert.Equal(e2, sink.Changes[0].Entity);
        Assert.Equal(7, sink.Changes[0].Old);   // stale slot from e1
        Assert.Equal(42, sink.Changes[0].New);
    }
}
