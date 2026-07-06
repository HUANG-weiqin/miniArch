using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class EntityCloneTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Clone_empty_entity()
    {
        var world = new World();
        var original = world.Create();

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

    [Fact]
    public void Clone_no_hierarchy_link()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create(new Position(1, 2));
        world.AddChild(parent, child);

        var clone = world.Clone(child);

        Assert.False(world.TryGetParent(clone, out _));
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

    [Fact]
    public void Deep_clone_copies_children()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child1 = world.Create(new Velocity(3, 4));
        var child2 = world.Create(new Health(100));
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

        var clone = world.Clone(parent);

        var cloneChildren = world.EnumerateChildren(clone).ToChildList();
        Assert.Equal(2, cloneChildren.Count);
        Assert.Contains(cloneChildren, child => world.TryGet(child, out Velocity _));
        Assert.Contains(cloneChildren, child => world.TryGet(child, out Health _));
    }

    [Fact]
    public void Deep_clone_preserves_hierarchy()
    {
        var world = new World();
        var root = world.Create(new Position(1, 2));
        var mid = world.Create(new Velocity(3, 4));
        var leaf = world.Create(new Health(100));
        world.AddChild(root, mid);
        world.AddChild(mid, leaf);

        var clone = world.Clone(root);

        var cloneMids = world.EnumerateChildren(clone).ToChildList();
        Assert.Single(cloneMids);
        var cloneMid = cloneMids[0];
        Assert.True(world.TryGet(cloneMid, out Velocity _));

        var cloneLeaves = world.EnumerateChildren(cloneMid).ToChildList();
        Assert.Single(cloneLeaves);
        Assert.True(world.TryGet(cloneLeaves[0], out Health _));
    }

    [Fact]
    public void Deep_clone_root_has_no_parent()
    {
        var world = new World();
        var grandparent = world.Create();
        var parent = world.Create(new Position(1, 2));
        var child = world.Create(new Velocity(3, 4));
        world.AddChild(grandparent, parent);
        world.AddChild(parent, child);

        var clone = world.Clone(parent);

        Assert.False(world.TryGetParent(clone, out _));
    }

    [Fact]
    public void Deep_clone_is_independent_from_original()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child = world.Create(new Velocity(3, 4));
        world.AddChild(parent, child);

        var clone = world.Clone(parent);
        world.Set(clone, new Position(99, 99));

        Assert.True(world.TryGet(parent, out Position origPos));
        Assert.Equal(new Position(1, 2), origPos);
    }

    [Fact]
    public void Deep_clone_destroy_original_does_not_affect_clone()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child = world.Create(new Velocity(3, 4));
        world.AddChild(parent, child);

        var clone = world.Clone(parent);
        world.Destroy(parent);

        Assert.True(world.IsAlive(clone));
        Assert.Single(world.EnumerateChildren(clone).ToChildList());
    }

    [Fact]
    public void Deep_clone_entity_without_children_works()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        var clone = world.Clone(entity);

        Assert.True(world.IsAlive(clone));
        Assert.False(world.HasChildren(clone));
        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(1, 2), pos);
    }
}

public sealed class CommandBufferCloneTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

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
        var source = world.Create();
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
    public void Deep_clone_copies_children_after_submit()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child1 = world.Create(new Velocity(3, 4));
        var child2 = world.Create(new Health(100));
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(parent);
        buffer.Submit();

        var cloneChildren = world.EnumerateChildren(clone).ToChildList();
        Assert.Equal(2, cloneChildren.Count);
    }

    [Fact]
    public void Deep_clone_preserves_hierarchy_after_submit()
    {
        var world = new World();
        var root = world.Create(new Position(1, 2));
        var mid = world.Create(new Velocity(3, 4));
        var leaf = world.Create(new Health(100));
        world.AddChild(root, mid);
        world.AddChild(mid, leaf);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(root);
        buffer.Submit();

        var cloneMids = world.EnumerateChildren(clone).ToChildList();
        Assert.Single(cloneMids);
        var cloneLeaves = world.EnumerateChildren(cloneMids[0]).ToChildList();
        Assert.Single(cloneLeaves);
        Assert.True(world.TryGet(cloneLeaves[0], out Health _));
    }

    [Fact]
    public void Deep_clone_root_has_no_parent_after_submit()
    {
        var world = new World();
        var grandparent = world.Create();
        var parent = world.Create(new Position(1, 2));
        world.AddChild(grandparent, parent);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(parent);
        buffer.Submit();

        Assert.False(world.TryGetParent(clone, out _));
    }

    [Fact]
    public void Deep_clone_with_set_on_root()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child = world.Create(new Velocity(3, 4));
        world.AddChild(parent, child);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(parent);
        buffer.Set(clone, new Position(99, 99));
        buffer.Submit();

        Assert.True(world.TryGet(clone, out Position pos));
        Assert.Equal(new Position(99, 99), pos);
        Assert.Single(world.EnumerateChildren(clone).ToChildList());
    }

    [Fact]
    public void Deep_clone_with_remove_on_root()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2), new Velocity(3, 4));
        var child = world.Create(new Health(100));
        world.AddChild(parent, child);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(parent);
        buffer.Remove<Position>(clone);
        buffer.Submit();

        Assert.False(world.TryGet<Position>(clone, out _));
        Assert.True(world.TryGet(clone, out Velocity vel));
        Assert.Equal(new Velocity(3, 4), vel);
        Assert.Single(world.EnumerateChildren(clone).ToChildList());
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

    [Fact]
    public void Clone_throws_after_destroy_pending_source_in_same_buffer()
    {
        var world = new World();
        var buffer = new CommandStream(world);

        var source = buffer.Create();
        buffer.Add(source, new Position(1, 2));
        buffer.Destroy(source);
        Assert.Throws<InvalidOperationException>(() => buffer.Clone(source));
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
    public void Deep_clone_destroy_cloned_entity_in_buffer()
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
    public void Deep_clone_destroy_cloned_subtree_in_buffer_does_not_materialize_children()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2));
        var child = world.Create(new Health(100));
        world.AddChild(source, child);
        var buffer = new CommandStream(world);

        var clone = buffer.Clone(source);
        buffer.Destroy(clone);
        buffer.Submit();

        Assert.False(world.IsAlive(clone));
        var description = new QueryDescription().With<Health>();
        Assert.Equal(1, CountEntities(MiniQueryCache.Create(world, in description)));
    }

    [Fact]
    public void Clone_virtual_hierarchy_pending_AddChild_materialized_child()
    {
        var world = new World();
        var father = world.Create(new Position(1, 2));       // materialized source
        var son = world.Create(new Health(100));              // materialized existing
        var buffer = new CommandStream(world);

        buffer.AddChild(father, son);
        var cloneFather = buffer.Clone(father);               // record-time snapshot
        buffer.Submit();

        // cloneFather should have a child (the clone of son)
        var children = world.EnumerateChildren(cloneFather).ToChildList();
        Assert.Single(children);
        Assert.True(world.TryGet(children[0], out Health _));
        // original son still has father as parent (not moved)
        Assert.True(world.TryGetParent(son, out var origParent));
        Assert.Equal(father, origParent);
    }

    [Fact]
    public void Clone_virtual_hierarchy_RemoveChild_excludes_from_children()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child = world.Create(new Health(100));
        world.AddChild(parent, child);
        var buffer = new CommandStream(world);

        buffer.RemoveChild(child);
        var clone = buffer.Clone(parent);
        buffer.Submit();

        // clone should have NO children (RemoveChild was pending)
        Assert.False(world.HasChildren(clone));
    }

    [Fact]
    public void Clone_virtual_hierarchy_pending_AddChild_pending_child()
    {
        var world = new World();
        var buffer = new CommandStream(world);

        var father = buffer.Create();
        var son = buffer.Create();
        buffer.Add(son, new Health(50));
        buffer.AddChild(father, son);

        var cloneFather = buffer.Clone(father);
        buffer.Submit();

        // cloneFather has a child (deep clone of son)
        var children = world.EnumerateChildren(cloneFather).ToChildList();
        Assert.Single(children);
        Assert.True(world.TryGet(children[0], out Health h));
        Assert.Equal(new Health(50), h);
    }

    [Fact]
    public void Clone_virtual_hierarchy_cycle_throws()
    {
        var world = new World();
        var buffer = new CommandStream(world);

        var a = buffer.Create();
        var b = buffer.Create();
        buffer.AddChild(a, b);
        // Create a cycle: b's parent = a (already), and a's parent = b
        buffer.AddChild(b, a);

        Assert.Throws<InvalidOperationException>(() => buffer.Clone(a));
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
    public void Parallel_clone_pending_source_limitation_documented()
    {
        // NOTE: ParallelCommandStream does not support pending source Clone.
        // This test verifies that materialized source Clone still works.
        var world = new World();
        var buffer = new ParallelCommandStream(world);
        var source = world.Create(new Position(1, 2));
        var clone = buffer.Clone(source);  // materialized, should work
        buffer.Submit();
        Assert.True(world.IsAlive(clone));
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
    public void Clone_with_children_does_not_allocate()
    {
        var world = new World();
        var parent = world.Create(new Position(1, 2));
        var child1 = world.Create(new Velocity(3, 4));
        var child2 = world.Create(new Health(100));
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);
        var buffer = new CommandStream(world);

        // Warm up
        buffer.Clone(parent); buffer.Submit();
        buffer.Clone(parent); buffer.Submit();

        // Measure on same buffer
        var before = GC.GetAllocatedBytesForCurrentThread();
        buffer.Clone(parent);
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        Assert.True(allocated <= 1,
            $"Clone_with_children allocated {allocated} bytes, expected ~0. " +
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
    public void Deep_clone_preserves_hierarchy_in_buffer()
    {
        var world = new World();
        var root = world.Create(new Position(1, 2));
        var mid = world.Create(new Velocity(3, 4));
        var leaf = world.Create(new Health(100));
        world.AddChild(root, mid);
        world.AddChild(mid, leaf);
        var buffer = new CommandStream(world);
        var clone = buffer.Clone(root);
        buffer.Submit();

        var cloneMids = world.EnumerateChildren(clone).ToChildList();
        Assert.Single(cloneMids);
        var cloneMid = cloneMids[0];
        Assert.True(world.TryGet(cloneMid, out Velocity _));

        var cloneLeaves = world.EnumerateChildren(cloneMid).ToChildList();
        Assert.Single(cloneLeaves);
        Assert.True(world.TryGet(cloneLeaves[0], out Health _));
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

    [Fact]
    public void Parallel_clone_pending_source_throws()
    {
        var world = new World();
        var buffer = new ParallelCommandStream(world);
        var src = buffer.Create();
        buffer.Add(src, new Position(1, 2));

        Assert.Throws<NotSupportedException>(() => buffer.Clone(src));
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

    // ═══════════════════════════════════════════════════════════════════════
    // Deterministic fuzz: Clone + basic ops, Submit == Replay convergence
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Long-duration deterministic fuzz: 10 000 frames of random Create/Destroy/
    /// Set/Clone operations. Verifies every 1000 frames that Submit and
    /// Snapshot+Replay produce the same entity count, and at the end that the
    /// two worlds are byte-identical.
    ///
    /// This is NOT a correctness test for individual Clone semantics — those are
    /// covered by the 42+ targeted unit tests above. This verifies the SUBMIT ==
    /// REPLAY convergence invariant under prolonged randomized sequences including
    /// Clone, which is the property that hand-written tests cannot exhaustively
    /// cover.
    /// </summary>
    [Fact]
    public void Clone_fuzz_10000_frames_seed_42_submit_and_replay_converge()
    {
        RunCloneFuzz(seed: 42, frames: 10000, convergeCheckInterval: 1000);
    }

    /// <summary>
    /// Multi-seed variant: 101 seeds × 100 frames + 3 edge seeds × 100 frames.
    /// Catches seed-specific divergent sequences.
    /// </summary>
    [Fact]
    public void Clone_fuzz_multi_seed_submit_and_replay_converge()
    {
        for (var s = 0; s <= 100; s++)
            RunCloneFuzz(s, frames: 100, convergeCheckInterval: 10);
        foreach (var s in new[] { 65535, 999999, 2147483647 })
            RunCloneFuzz(s, frames: 100, convergeCheckInterval: 10);
    }

    private static void RunCloneFuzz(int seed, int frames, int convergeCheckInterval)
    {
        var world = new World();
        var replica = new World();
        var stream = new CommandStream(world);
        var alive = new List<Entity>();
        var rng = new Random(seed);

        for (var frame = 0; frame < frames; frame++)
        {
            // ── Remove entities killed by cascade or cross-frame Destroy ──
            alive.RemoveAll(e => !world.IsAlive(e));

            // ── Build per-frame tracking from actual World state ──
            var tracked = new HashSet<Entity>(alive);
            var hasPos = new HashSet<Entity>();
            var hasVel = new HashSet<Entity>();
            foreach (var e in alive)
            {
                if (world.Has<Position>(e)) hasPos.Add(e);
                if (world.Has<Velocity>(e)) hasVel.Add(e);
            }

            // ── Generate random operations ──
            var opsThisFrame = rng.Next(1, 10);
            for (var op = 0; op < opsThisFrame; op++)
            {
                var kind = alive.Count == 0 ? 0 : rng.Next(100);

                if (kind < 30 || alive.Count == 0)
                {
                    // Create entity (pending at record time)
                    var e = stream.Create();
                    if (rng.Next(2) == 0) stream.Add(e, new Position(rng.Next(), rng.Next()));
                    if (rng.Next(2) == 0) stream.Add(e, new Velocity(rng.Next(), rng.Next()));
                    alive.Add(e);
                    tracked.Add(e);
                }
                else if (kind < 45)
                {
                    // Destroy existing entity (may be materialized or pending)
                    var i = rng.Next(alive.Count);
                    var e = alive[i];
                    if (tracked.Contains(e))
                    {
                        stream.Destroy(e);
                        tracked.Remove(e);
                    }
                }
                else if (kind < 75)
                {
                    // Set component on materialized entity (must have component)
                    var i = rng.Next(alive.Count);
                    var e = alive[i];
                    if (!tracked.Contains(e)) continue;
                    if (hasPos.Contains(e))
                        stream.Set(e, new Position(rng.Next(), rng.Next()));
                    else if (hasVel.Contains(e))
                        stream.Set(e, new Velocity(rng.Next(), rng.Next()));
                    // else: entity with no known component → skip Set
                }
                else
                {
                    // Clone from an entity that is alive and not destroyed this frame
                    var valid = alive.Where(e => tracked.Contains(e)).ToArray();
                    if (valid.Length > 0)
                    {
                        var src = valid[rng.Next(valid.Length)];
                        var clone = stream.Clone(src);
                        alive.Add(clone);
                        // Clone copies source's components; we don't track them here
                        // because next frame will re-read from world state.
                    }
                }
            }

            // ── Snapshot → Submit → Replay ──
            var delta = stream.Snapshot();
            stream.Submit();

            try
            {
                new CommandStream(replica).Replay(FrameDelta.FromWire(delta.AsSpan()));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Replay failed during Clone fuzz seed={seed}, frame={frame}.", ex);
            }

            // ── Periodic convergence check ──
            if ((frame + 1) % convergeCheckInterval == 0)
                Assert.Equal(world.EntityCount, replica.EntityCount);
        }

        // ── Final: full checksum convergence ──
        var worldHash = world.CanonicalChecksum();
        var replicaHash = replica.CanonicalChecksum();
        Assert.True(worldHash.SequenceEqual(replicaHash),
            $"Worlds diverged after {frames} frames, seed={seed}.\n" +
            $"  Source ec={world.EntityCount}, Replica ec={replica.EntityCount}");
    }
}
