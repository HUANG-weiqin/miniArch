using MiniArch.Tests.Core.TestSupport;

namespace MiniArch.Tests.Core;

public sealed class ChildrenEnumerableTests
{
    [Fact]
    public void EnumerateChildren_on_entity_with_no_children_returns_empty()
    {
        var world = new World();
        var entity = world.CreateEmpty();

        var children = world.EnumerateChildren(entity).ToChildList();

        Assert.Empty(children);
    }

    [Fact]
    public void EnumerateChildren_with_single_child_returns_that_child()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        world.AddChild(parent, child);

        var children = world.EnumerateChildren(parent).ToChildList();

        Assert.Single(children);
        Assert.Equal(child, children[0]);
    }

    [Fact]
    public void EnumerateChildren_with_multiple_children_returns_all()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.CreateEmpty();
        var child2 = world.CreateEmpty();
        var child3 = world.CreateEmpty();
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);
        world.AddChild(parent, child3);

        var children = world.EnumerateChildren(parent).ToChildList();

        Assert.Equal(3, children.Count);
        Assert.Contains(child1, children);
        Assert.Contains(child2, children);
        Assert.Contains(child3, children);
    }

    [Fact]
    public void EnumerateChildren_skips_destroyed_child()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.CreateEmpty();
        var child2 = world.CreateEmpty();
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

        world.Destroy(child1);
        var children = world.EnumerateChildren(parent).ToChildList();

        Assert.Single(children);
        Assert.Equal(child2, children[0]);
    }

    [Fact]
    public void EnumerateChildren_skips_only_destroyed_children()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.CreateEmpty();
        var child2 = world.CreateEmpty();
        var child3 = world.CreateEmpty();
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);
        world.AddChild(parent, child3);

        world.Destroy(child2);
        var children = world.EnumerateChildren(parent).ToChildList();

        Assert.Equal(2, children.Count);
        Assert.Contains(child1, children);
        Assert.Contains(child3, children);
    }

    [Fact]
    public void EnumerateChildren_returns_empty_after_all_children_destroyed()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child1 = world.CreateEmpty();
        var child2 = world.CreateEmpty();
        world.AddChild(parent, child1);
        world.AddChild(parent, child2);

        world.Destroy(child1);
        world.Destroy(child2);
        var children = world.EnumerateChildren(parent).ToChildList();

        Assert.Empty(children);
    }

    [Fact]
    public void EnumerateChildren_is_zero_alloc()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        world.AddChild(parent, child);

        // ChildrenEnumerable is a struct, GetEnumerator returns a struct
        // No heap allocation should occur
        var enumerable = world.EnumerateChildren(parent);
        var enumerator = enumerable.GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(child, enumerator.Current);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void Multiple_EnumerateChildren_calls_are_independent()
    {
        var world = new World();
        var parent1 = world.CreateEmpty();
        var parent2 = world.CreateEmpty();
        var child1 = world.CreateEmpty();
        var child2 = world.CreateEmpty();
        world.AddChild(parent1, child1);
        world.AddChild(parent2, child2);

        var children1 = world.EnumerateChildren(parent1).ToChildList();
        var children2 = world.EnumerateChildren(parent2).ToChildList();

        Assert.Single(children1);
        Assert.Equal(child1, children1[0]);
        Assert.Single(children2);
        Assert.Equal(child2, children2[0]);
    }

    [Fact]
    public void EnumerateChild_on_orphaned_entity_returns_empty()
    {
        var world = new World();
        var entity = world.CreateEmpty();
        world.Destroy(entity);

        // Entity is dead, but EnumerateChildren on any valid parent entity
        // that has no children should return empty regardless
        var parent = world.CreateEmpty();
        var children = world.EnumerateChildren(parent).ToChildList();
        Assert.Empty(children);
    }

    [Fact]
    public void EnumerateChildren_does_not_include_unrelated_child()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var orphan = world.CreateEmpty();
        // orphan has no parent, so EnumerateChildren(parent) should not see it

        var children = world.EnumerateChildren(parent).ToChildList();
        Assert.Empty(children);
    }

    [Fact]
    public void EnumerateChildren_after_RemoveChild_does_not_include_removed()
    {
        var world = new World();
        var parent = world.CreateEmpty();
        var child = world.CreateEmpty();
        world.AddChild(parent, child);
        world.RemoveChild(child);

        var children = world.EnumerateChildren(parent).ToChildList();
        Assert.Empty(children);
    }

    [Fact]
    public void EnumerateChildren_after_child_reparented_to_another_parent_shows_under_new_parent()
    {
        var world = new World();
        var parent1 = world.CreateEmpty();
        var parent2 = world.CreateEmpty();
        var child = world.CreateEmpty();

        world.AddChild(parent1, child);
        world.AddChild(parent2, child);

        var underParent1 = world.EnumerateChildren(parent1).ToChildList();
        var underParent2 = world.EnumerateChildren(parent2).ToChildList();

        Assert.Empty(underParent1);
        Assert.Single(underParent2);
        Assert.Equal(child, underParent2[0]);
    }
}
