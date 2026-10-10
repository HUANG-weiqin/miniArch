using System.Reflection;
using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class EntityCloneTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);
    private readonly record struct EntityReference(Entity Target);

    [Fact]
    public void Clone_empty_entity()
    {
        var world = new World();
        var original = world.CreateEmpty();

        var clone = world.Clone(original);

        Assert.True(world.IsAlive(clone));
        Assert.NotEqual(original, clone);
    }

    [Fact]
    public void Clone_copies_all_components()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2), new Velocity(3, 4), new Health(100));

        var clone = world.Clone(original);

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.True(world.TryGet(clone, out Velocity vel));
        Assert.True(world.TryGet(clone, out Health hp));
        Assert.Equal(new Position(1, 2), pos);
        Assert.Equal(new Velocity(3, 4), vel);
        Assert.Equal(new Health(100), hp);
    }

    [Fact]
    public void Clone_single_component()
    {
        var world = new World();
        var original = world.Create(new Position(10, 20));

        var clone = world.Clone(original);

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(10, 20), pos);
    }

    [Fact]
    public void Clone_lands_in_same_archetype()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2), new Velocity(3, 4));

        var clone = world.Clone(original);

        Assert.True(world.TryGetLocation(original, out var origInfo));
        Assert.True(world.TryGetLocation(clone, out var cloneInfo));
        Assert.Same(origInfo.Archetype, cloneInfo.Archetype);
    }

    [Fact]
    public void Clone_is_independent()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2));

        var clone = world.Clone(original);

        world.Set(clone, new Position(99, 99));
        Assert.True(world.TryGet(original, out Position origPos));
        Assert.Equal(new Position(1, 2), origPos);
        Assert.True(world.TryGet(clone, out Position clonePos));
        Assert.Equal(new Position(99, 99), clonePos);
    }

    [Fact]
    public void Clone_destroy_original_does_not_affect_clone()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2));

        var clone = world.Clone(original);
        world.Destroy(original);

        Assert.False(world.IsAlive(original));
        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(1, 2), pos);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clone_preserves_ordinary_entity_reference_without_cloning_target(bool useCommandStream)
    {
        using var world = new World();
        var target = world.Create(new Health(42));
        var source = world.Create(new EntityReference(target));

        Entity clone;
        if (useCommandStream)
        {
            var stream = new CommandStream(world);
            clone = stream.Clone(source);
            stream.Submit();
        }
        else
        {
            clone = world.Clone(source);
        }

        Assert.Equal(3, world.EntityCount);
        Assert.True(world.TryGet(clone, out EntityReference reference));
        Assert.Equal(target, reference.Target);
        Assert.True(world.IsAlive(target));
    }

    [Fact]
    public void Clone_throws_on_dead_entity()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        world.Destroy(entity);

        Assert.Throws<InvalidOperationException>(() => world.Clone(entity));
    }

    [Fact]
    public void Clone_multiple_from_same_source()
    {
        var world = new World();
        var original = world.Create(new Position(1, 2), new Health(100));

        var clone1 = world.Clone(original);
        var clone2 = world.Clone(original);

        Assert.True(world.IsAlive(clone1));
        Assert.True(world.IsAlive(clone2));
        Assert.NotEqual(clone1, clone2);

        Assert.True(world.TryGet(clone1, out Position pos1));
        Assert.True(world.TryGet(clone2, out Position pos2));
        Assert.Equal(new Position(1, 2), pos1);
        Assert.Equal(new Position(1, 2), pos2);
    }

}

public sealed class CommandBufferCloneTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);
    private readonly struct CloneMarker0 { }
    private readonly struct CloneMarker1 { }
    private readonly struct CloneMarker2 { }
    private readonly struct CloneMarker3 { }
    private readonly struct CloneMarker4 { }
    private readonly struct CloneMarker5 { }
    private readonly struct CloneMarker6 { }
    private readonly struct CloneMarker7 { }
    private readonly struct CloneMarker8 { }
    private readonly struct CloneComponent<TLeft, TRight>
        where TLeft : unmanaged
        where TRight : unmanaged
    { }

    [Fact]
    public void Clone_creates_deferred_entity_with_components()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        Assert.False(world.IsAlive(clone));

        buffer.Submit();

        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position pos));
        Assert.True(world.TryGet(clone, out Velocity vel));
        Assert.Equal(new Position(1, 2), pos);
        Assert.Equal(new Velocity(3, 4), vel);
    }

    [Fact]
    public void Clone_empty_entity()
    {
        var world = new World();
        var source = world.CreateEmpty();
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.True(world.IsAlive(clone));
        Assert.NotEqual(source, clone);
    }

    [Fact]
    public void Clone_is_independent_after_submit()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Submit();

        world.Set(clone, new Position(99, 99));
        Assert.True(world.TryGet(source, out Position srcPos));
        Assert.Equal(new Position(1, 2), srcPos);
    }

    [Fact]
    public void Clone_throws_on_dead_entity()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));
        world.Destroy(entity);
        var buffer = new CommandStream(world);

        Assert.Throws<InvalidOperationException>(() => buffer.Clone(entity));
    }

    [Fact]
    public void Clone_with_other_commands_in_same_buffer()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var cloned = buffer.Clone(source);
        var created = buffer.Create();
        buffer.Add(created, new Health(50));

        buffer.Submit();

        Assert.True(world.IsAlive(cloned));
        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(cloned, out Position pos));
        Assert.Equal(new Position(1, 2), pos);
        Assert.True(world.TryGet(created, out Health hp));
        Assert.Equal(new Health(50), hp);
    }

    [Fact]
    public void Clone_can_be_modified_after_submit()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Submit();

        world.Remove<Velocity>(clone);
        Assert.False(world.TryGet<Velocity>(clone, out _));
        Assert.True(world.TryGet<Position>(clone, out _));
    }

    [Fact]
    public void Clone_lands_in_same_archetype_after_submit()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.True(world.TryGetLocation(source, out var srcInfo));
        Assert.True(world.TryGetLocation(clone, out var cloneInfo));
        Assert.Same(srcInfo.Archetype, cloneInfo.Archetype);
    }

    [Fact]
    public void Multiple_clones_from_same_source()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var clone1 = buffer.Clone(source);
        var clone2 = buffer.Clone(source);
        buffer.Submit();

        Assert.True(world.IsAlive(clone1));
        Assert.True(world.IsAlive(clone2));
        Assert.NotEqual(clone1, clone2);

        Assert.True(world.TryGet(clone1, out Position pos1));
        Assert.True(world.TryGet(clone2, out Position pos2));
        Assert.Equal(new Position(1, 2), pos1);
        Assert.Equal(new Position(1, 2), pos2);
    }

    [Fact]
    public void Clone_three_components()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4), new Health(100));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.True(world.TryGet(clone, out Velocity vel));
        Assert.True(world.TryGet(clone, out Health hp));
        Assert.Equal(new Position(1, 2), pos);
        Assert.Equal(new Velocity(3, 4), vel);
        Assert.Equal(new Health(100), hp);
    }

    [Fact]
    public void Clone_with_set_on_entity()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(parent);
        buffer.Set(clone, new Position(99, 99));
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(99, 99), pos);
    }

    [Fact]
    public void Clone_with_remove_on_entity()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(parent);
        buffer.Remove<Position>(clone);
        buffer.Submit();

        Assert.False(world.TryGet<Position>(clone, out _));
        Assert.True(world.TryGet(clone, out Velocity vel));
        Assert.Equal(new Velocity(3, 4), vel);
    }

    [Fact]
    public void Clone_snapshots_source_at_record_time_before_world_changes()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        world.Set(source, new Position(9, 9));
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position clonePos));
        Assert.Equal(new Position(1, 2), clonePos);
        Assert.True(world.TryGet(source, out Position sourcePos));
        Assert.Equal(new Position(9, 9), sourcePos);
    }

    [Fact]
    public void Clone_reflects_pending_source_remove_in_same_buffer()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        buffer.Remove<Position>(source);
        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.False(world.TryGet<Position>(source, out _));
        // CLONE BEHAVIOR FLIPPED: clone no longer has Position
        Assert.False(world.TryGet<Position>(clone, out _));
        Assert.True(world.TryGet(clone, out Velocity _));
    }

    [Fact]
    public void Clone_pending_source_sees_last_write_win()
    {
        var world = new World();
        var buffer = new CommandStream(world);

        var source = buffer.Create();
        buffer.Add(source, new Position(1, 2));
        buffer.Set(source, new Position(3, 4));  // overwrite

        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(3, 4), pos);  // last-wins
    }

    [Fact]
    public void Clone_pending_source_remove_component()
    {
        var world = new World();
        var buffer = new CommandStream(world);

        var source = buffer.Create();
        buffer.Add(source, new Position(1, 2));
        buffer.Add(source, new Velocity(3, 4));
        buffer.Remove<Position>(source);

        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.False(world.TryGet<Position>(clone, out _));
        Assert.True(world.TryGet(clone, out Velocity _));
    }

    [Fact]
    public void Clone_throws_after_destroy_in_same_buffer()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        buffer.Destroy(source);
        Assert.Throws<InvalidOperationException>(() => buffer.Clone(source));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void Clone_throws_after_destroy_pending_source_in_same_buffer(
        bool deferredEntities, int expectedDeltaCount)
    {
        using var world = new World();
        var buffer = new CommandStream(world) { DeferredEntities = deferredEntities };

        var source = buffer.Create();
        buffer.Add(source, new Position(1, 2));
        buffer.Destroy(source);

        Assert.Throws<InvalidOperationException>(() => buffer.Clone(source));
        var delta = buffer.Snapshot();
        Assert.Equal(expectedDeltaCount, delta.DeltaCount);
        delta.Validate();
        using var checkpoint = world.CaptureState();
    }

    [Fact]
    public void Clone_source_then_destroy_in_same_buffer_allows_clone()
    {
        // Clone before Destroy should work fine
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Destroy(source);
        buffer.Submit();

        Assert.False(world.IsAlive(source));
        Assert.True(world.IsAlive(clone));
        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(1, 2), pos);
    }

    [Fact]
    public void Clone_destroy_cloned_entity_in_buffer()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Destroy(clone);
        buffer.Submit();

        Assert.False(world.IsAlive(clone));
    }

    [Fact]
    public void BUG_clone_materialized_source_with_more_than_64_components()
    {
        var world = new World();
        var source = world.CreateEmpty();
        var markerTypes = new[]
        {
            typeof(CloneMarker0), typeof(CloneMarker1), typeof(CloneMarker2),
            typeof(CloneMarker3), typeof(CloneMarker4), typeof(CloneMarker5),
            typeof(CloneMarker6), typeof(CloneMarker7), typeof(CloneMarker8),
        };
        var addDefault = typeof(CommandBufferCloneTests).GetMethod(
            nameof(AddDefaultComponent), BindingFlags.NonPublic | BindingFlags.Static)!;

        for (var i = 0; i < 65; i++)
        {
            var type = typeof(CloneComponent<,>).MakeGenericType(
                markerTypes[i / markerTypes.Length], markerTypes[i % markerTypes.Length]);
            addDefault.MakeGenericMethod(type).Invoke(null, [world, source]);
        }
        Assert.True(world.TryGetLocation(source, out var sourceInfo));
        Assert.Equal(65, sourceInfo.Archetype.Signature.Count);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.True(world.TryGetLocation(clone, out var cloneInfo));
        Assert.Same(sourceInfo.Archetype, cloneInfo.Archetype);
    }

    [Fact]
    public void Clone_pending_source_submit_equals_replay()
    {
        var world1 = new World();
        var buffer1 = new CommandStream(world1);

        var src = buffer1.Create();
        buffer1.Add(src, new Position(1, 2));
        buffer1.Clone(src);
        buffer1.Submit();

        // Replay path
        var world2 = new World();
        var buffer2 = new CommandStream(world2) { DeferredEntities = true };
        var src2 = buffer2.Create();
        buffer2.Add(src2, new Position(1, 2));
        buffer2.Clone(src2);
        var delta = buffer2.Snapshot();
        buffer2.Clear();

        new CommandStream(world2).Replay(delta);

        // Both worlds should have exactly 2 entities with Position
        var q1Desc = new QueryDescription().With<Position>();
        var q2Desc = new QueryDescription().With<Position>();
        var q1 = CountEntities(MiniQueryCache.Create(world1, in q1Desc));
        var q2 = CountEntities(MiniQueryCache.Create(world2, in q2Desc));
        Assert.Equal(2, q1);
        Assert.Equal(q1, q2);
    }

    [Fact]
    public void Clone_materialized_overlay_submit_equals_replay()
    {
        var world1 = new World();
        var src1 = world1.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer1 = new CommandStream(world1);
        buffer1.Remove<Position>(src1);
        buffer1.Clone(src1);
        buffer1.Submit();

        var world2 = new World();
        var src2 = world2.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer2 = new CommandStream(world2) { DeferredEntities = true };
        buffer2.Remove<Position>(src2);
        buffer2.Clone(src2);
        var delta = buffer2.Snapshot();
        buffer2.Clear();
        new CommandStream(world2).Replay(delta);

        // Both worlds: source with Velocity only, clone with Velocity only
        var q1vDesc = new QueryDescription().With<Velocity>();
        var q2vDesc = new QueryDescription().With<Velocity>();
        var q1v = CountEntities(MiniQueryCache.Create(world1, in q1vDesc));
        var q2v = CountEntities(MiniQueryCache.Create(world2, in q2vDesc));
        Assert.Equal(2, q1v);
        Assert.Equal(q1v, q2v);

        var q1pDesc = new QueryDescription().With<Position>();
        var q2pDesc = new QueryDescription().With<Position>();
        var q1p = CountEntities(MiniQueryCache.Create(world1, in q1pDesc));
        var q2p = CountEntities(MiniQueryCache.Create(world2, in q2pDesc));
        Assert.Equal(0, q1p);
        Assert.Equal(q1p, q2p);
    }

    [Fact]
    public void Clone_materialized_source_does_not_allocate()
    {
        var world = new World();
        var src = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        // Add overlay to force store scan (Remove + Set)
        buffer.Remove<Velocity>(src);
        buffer.Set(src, new Position(99, 99));

        // Warm up: let batch buffer and internal arrays pre-allocate
        buffer.Clone(src); buffer.Submit();
        buffer.Clone(src); buffer.Submit();

        // Measure on same buffer (arrays already grown)
        var before = GC.GetAllocatedBytesForCurrentThread();
        buffer.Clone(src);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Temporary Lists and lambda closures should be eliminated.
        // Allow tiny overhead (≤1 byte) for edge cases like batch buffer growth.
        Assert.True(allocated <= 1,
            $"Clone_materialized_source allocated {allocated} bytes, expected ~0. " +
            "Temporary Lists and lambda closures should be eliminated.");
    }

    [Fact]
    public void Clone_pending_source_does_not_allocate()
    {
        var world = new World();
        var buffer = new CommandStream(world);

        // Source is a pending entity (created in buffer, not yet submitted)
        var src = buffer.Create();
        buffer.Add(src, new Position(1, 2));
        buffer.Add(src, new Velocity(3, 4));
        buffer.Set(src, new Position(99, 99));

        // Warm up
        buffer.Clone(src); buffer.Submit();

        // Re-create source in same buffer for measurement
        src = buffer.Create();
        buffer.Add(src, new Position(1, 2));
        buffer.Add(src, new Velocity(3, 4));
        buffer.Set(src, new Position(99, 99));

        var before = GC.GetAllocatedBytesForCurrentThread();
        buffer.Clone(src);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        Assert.True(allocated <= 1,
            $"Clone_pending_source allocated {allocated} bytes, expected ~0. " +
            "Temporary Lists and lambda closures should be eliminated.");
    }

    [Fact]
    public void Clone_materialized_source_pending_Set_overlay()
    {
        var world = new World();
        var src = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);
        buffer.Set(src, new Position(5, 6));
        var clone = buffer.Clone(src);
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(5, 6), pos);
    }

    [Fact]
    public void Clone_materialized_source_pending_Add_overlay()
    {
        var world = new World();
        var src = world.Create(new Position(1, 2));
        var buffer = new CommandStream(world);
        buffer.Add(src, new Velocity(3, 4));
        var clone = buffer.Clone(src);
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position _));
        Assert.True(world.TryGet(clone, out Velocity v));
        Assert.Equal(new Velocity(3, 4), v);
    }

    [Fact]
    public void Clone_snapshot_isolation_between_two_clones()
    {
        var world = new World();
        var src = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        var clone1 = buffer.Clone(src);
        buffer.Set(src, new Position(9, 9));
        var clone2 = buffer.Clone(src);
        buffer.Submit();

        Assert.True(world.TryGet(clone1, out Position pos1));
        Assert.Equal(new Position(1, 2), pos1);

        Assert.True(world.TryGet(clone2, out Position pos2));
        Assert.Equal(new Position(9, 9), pos2);
    }


    private static void AddDefaultComponent<T>(World world, Entity entity) where T : unmanaged
    {
        world.Add(entity, default(T));
    }

    private static int CountEntities(MiniQueryCache query)
    {
        var total = 0;
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            total += archetype.GetEntities().Length;
        }

        return total;
    }
}
