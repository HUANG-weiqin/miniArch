using MiniArch.Core;
using MiniArch.Tests.Core.TestSupport;
using MiniQuery = MiniArch.Core.QueryCache;

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
    public void Clone_ignores_pending_source_remove_in_same_buffer()
    {
        var world = new World();
        var source = world.Create(new Position(1, 2), new Velocity(3, 4));
        var buffer = new CommandStream(world);

        buffer.Remove<Position>(source);
        var clone = buffer.Clone(source);
        buffer.Submit();

        Assert.False(world.TryGet<Position>(source, out _));
        Assert.True(world.TryGet(clone, out Position clonePos));
        Assert.Equal(new Position(1, 2), clonePos);
        Assert.True(world.TryGet(clone, out Velocity _));
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
        Assert.Equal(1, CountEntities(MiniQuery.Create(world, in description)));
    }

    private static int CountEntities(MiniQuery query)
    {
        var total = 0;
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            total += archetype.GetEntities().Length;
        }

        return total;
    }
}
