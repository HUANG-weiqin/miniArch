using MiniArch.Tests.Core.TestSupport;

namespace MiniArch.Tests.Core;

public sealed class AncestorQueryTests
{
    // ══════════════════════════════════════════════════════
    // IsRoot
    // ══════════════════════════════════════════════════════

    [Fact]
    public void IsRoot_returns_true_for_entity_with_no_parent()
    {
        var world = new World();
        var entity = world.Create();

        Assert.True(world.IsRoot(entity));
    }

    [Fact]
    public void IsRoot_returns_false_for_child_entity()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        Assert.False(world.IsRoot(child));
    }

    [Fact]
    public void IsRoot_returns_false_for_dead_entity()
    {
        var world = new World();
        var entity = world.Create();
        world.Destroy(entity);

        Assert.False(world.IsRoot(entity));
    }

    [Fact]
    public void IsRoot_returns_false_for_stale_handle()
    {
        var world = new World();
        var original = world.Create();
        world.Destroy(original);
        var recycled = world.Create();
        Assert.Equal(original.Id, recycled.Id);

        Assert.False(world.IsRoot(original));
    }

    [Fact]
    public void IsRoot_returns_true_when_parent_is_destroyed()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        // Remove child first (otherwise Destroy(parent) cascade-kills the child)
        world.RemoveChild(child);
        world.Destroy(parent);

        Assert.True(world.IsRoot(child));
    }

    [Fact]
    public void IsRoot_throws_after_world_disposed()
    {
        var world = new World();
        var entity = world.Create();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.IsRoot(entity));
    }

    // ══════════════════════════════════════════════════════
    // GetDepth
    // ══════════════════════════════════════════════════════

    [Fact]
    public void GetDepth_returns_0_for_root_entity()
    {
        var world = new World();
        var entity = world.Create();

        Assert.Equal(0, world.GetDepth(entity));
    }

    [Fact]
    public void GetDepth_returns_1_for_direct_child()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        Assert.Equal(0, world.GetDepth(parent));
        Assert.Equal(1, world.GetDepth(child));
    }

    [Fact]
    public void GetDepth_returns_correct_depth_for_deep_chain()
    {
        var world = new World();
        var e0 = world.Create(); // root
        var e1 = world.Create(); // depth 1
        var e2 = world.Create(); // depth 2
        var e3 = world.Create(); // depth 3
        world.AddChild(e0, e1);
        world.AddChild(e1, e2);
        world.AddChild(e2, e3);

        Assert.Equal(0, world.GetDepth(e0));
        Assert.Equal(1, world.GetDepth(e1));
        Assert.Equal(2, world.GetDepth(e2));
        Assert.Equal(3, world.GetDepth(e3));
    }

    [Fact]
    public void GetDepth_returns_negative_1_for_dead_entity()
    {
        var world = new World();
        var entity = world.Create();
        world.Destroy(entity);

        Assert.Equal(-1, world.GetDepth(entity));
    }

    [Fact]
    public void GetDepth_returns_negative_1_for_stale_handle()
    {
        var world = new World();
        var original = world.Create();
        world.Destroy(original);
        var recycled = world.Create();
        Assert.Equal(original.Id, recycled.Id);

        Assert.Equal(-1, world.GetDepth(original));
    }

    [Fact]
    public void GetDepth_throws_after_world_disposed()
    {
        var world = new World();
        var entity = world.Create();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.GetDepth(entity));
    }

    // ══════════════════════════════════════════════════════
    // EnumerateAncestors
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EnumerateAncestors_on_root_entity_returns_empty()
    {
        var world = new World();
        var entity = world.Create();

        Assert.Empty(world.EnumerateAncestors(entity).ToAncestorList());
    }

    [Fact]
    public void EnumerateAncestors_on_child_returns_parent()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        var ancestors = world.EnumerateAncestors(child).ToAncestorList();

        Assert.Single(ancestors);
        Assert.Equal(parent, ancestors[0]);
    }

    [Fact]
    public void EnumerateAncestors_yields_parent_grandparent_root()
    {
        var world = new World();
        var root = world.Create();
        var mid = world.Create();
        var leaf = world.Create();
        world.AddChild(root, mid);
        world.AddChild(mid, leaf);

        var ancestors = world.EnumerateAncestors(leaf).ToAncestorList();

        Assert.Equal(2, ancestors.Count);
        Assert.Equal(mid, ancestors[0]);   // immediate parent first
        Assert.Equal(root, ancestors[1]);  // grandparent last
    }

    [Fact]
    public void EnumerateAncestors_on_dead_entity_returns_empty()
    {
        var world = new World();
        var entity = world.Create();
        world.Destroy(entity);

        Assert.Empty(world.EnumerateAncestors(entity).ToAncestorList());
    }

    [Fact]
    public void EnumerateAncestors_on_stale_handle_returns_empty()
    {
        var world = new World();
        var original = world.Create();
        world.Destroy(original);
        var recycled = world.Create();
        Assert.Equal(original.Id, recycled.Id);

        Assert.Empty(world.EnumerateAncestors(original).ToAncestorList());
    }

    [Fact]
    public void EnumerateAncestors_stops_at_destroyed_parent()
    {
        var world = new World();
        var root = world.Create();
        var mid = world.Create();
        var leaf = world.Create();
        world.AddChild(root, mid);
        world.AddChild(mid, leaf);

        // Destroy mid — leaf should now see no ancestors (mid is dead, can't walk past it)
        world.Destroy(mid);

        var ancestors = world.EnumerateAncestors(leaf).ToAncestorList();

        // leaf's parent (mid) is dead → chain terminates, no ancestors yielded
        Assert.Empty(ancestors);
    }

    [Fact]
    public void EnumerateAncestors_does_not_include_self()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        var ancestors = world.EnumerateAncestors(child).ToAncestorList();

        Assert.DoesNotContain(child, ancestors);
    }

    [Fact]
    public void EnumerateAncestors_after_RemoveChild_returns_empty()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);
        world.RemoveChild(child);

        Assert.Empty(world.EnumerateAncestors(child).ToAncestorList());
    }

    [Fact]
    public void EnumerateAncestors_is_zero_alloc()
    {
        var world = new World();
        var parent = world.Create();
        var child = world.Create();
        world.AddChild(parent, child);

        var enumerable = world.EnumerateAncestors(child);
        var enumerator = enumerable.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(parent, enumerator.Current);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void EnumerateAncestors_stale_handle_after_slot_reuse_does_not_traverse_wrong_hierarchy()
    {
        // Regression: the enumerator must check liveness on _current before
        // looking up _parentByChild[id]. A stale handle pointing to a reused
        // slot would otherwise read a different entity's parent.
        var world = new World();
        var originalParent = world.Create();
        var originalChild = world.Create();
        world.AddChild(originalParent, originalChild);

        // Destroy the child, freeing its slot
        var childId = originalChild.Id;
        world.Destroy(originalChild);

        // Create a new entity that reuses the same slot directly
        var replacement = world.Create();
        Assert.Equal(childId, replacement.Id);

        // Give replacement a different parent
        var newParent = world.Create();
        world.AddChild(newParent, replacement);

        // The stale handle (originalChild) should yield no ancestors,
        // not inherit newParent as its ancestor.
        Assert.Empty(world.EnumerateAncestors(originalChild).ToAncestorList());
    }

    // ══════════════════════════════════════════════════════
    // Cycle detection
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EnumerateAncestors_throws_on_hierarchy_cycle()
    {
        var world = new World();
        var a = world.Create();
        var b = world.Create();

        // Inject a cycle: A → B → A
        world.AddChild(a, b);
        world.Hierarchy.SetParentForTest(a, b);

        var enumerator = world.EnumerateAncestors(a).GetEnumerator();

        // First step should detect the cycle and throw
        _ = Assert.Throws<InvalidOperationException>(() =>
        {
            while (enumerator.MoveNext()) { }
        });
    }

    [Fact]
    public void GetDepth_throws_on_hierarchy_cycle()
    {
        var world = new World();
        var a = world.Create();
        var b = world.Create();

        world.AddChild(a, b);
        world.Hierarchy.SetParentForTest(a, b);

        Assert.Throws<InvalidOperationException>(() => world.GetDepth(a));
    }

    // ══════════════════════════════════════════════════════
    // Dispose guard
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EnumerateAncestors_throws_after_world_disposed()
    {
        var world = new World();
        var entity = world.Create();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.EnumerateAncestors(entity));
    }
}
