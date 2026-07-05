using System.Collections.ObjectModel;
using MiniArch.Diagnostics;

namespace MiniArchTests.Persistence;

public sealed class WorldDiffTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Identical_worlds_produce_identical_result()
    {
        var worldA = new World();
        worldA.Create<Position, Velocity>(new Position(1, 2), new Velocity(3, 4));

        var worldB = new World();
        worldB.Create<Position, Velocity>(new Position(1, 2), new Velocity(3, 4));

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.True(diff.AreIdentical);
        Assert.Empty(diff.EntityDiffs);
        Assert.Null(diff.FreeListDiff);
    }

    [Fact]
    public void Entity_only_in_A_detected()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(1, 2));

        var worldB = new World();

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);
        Assert.Single(diff.EntityDiffs);
        Assert.Equal(EntityDiffKind.OnlyInA, diff.EntityDiffs[0].Kind);
        Assert.Equal(0, diff.EntityDiffs[0].EntityId);
    }

    [Fact]
    public void Entity_only_in_B_detected()
    {
        var worldA = new World();

        var worldB = new World();
        worldB.Create<Position>(new Position(1, 2));

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);
        Assert.Single(diff.EntityDiffs);
        Assert.Equal(EntityDiffKind.OnlyInB, diff.EntityDiffs[0].Kind);
    }

    [Fact]
    public void Same_entity_different_component_value_produces_component_diff()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(1, 2));

        var worldB = new World();
        worldB.Create<Position>(new Position(99, 100));

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);
        Assert.Single(diff.EntityDiffs);

        var ed = diff.EntityDiffs[0];
        Assert.Equal(EntityDiffKind.Different, ed.Kind);
        Assert.False(ed.VersionMismatch);
        Assert.Single(ed.ComponentDiffs);
        Assert.Equal(typeof(Position), ed.ComponentDiffs[0].ComponentType);
        Assert.Equal(ComponentDiffKind.ValueDifferent, ed.ComponentDiffs[0].Kind);
    }

    [Fact]
    public void Extra_component_in_A_produces_component_diff()
    {
        var worldA = new World();
        worldA.Create<Position, Velocity>(new Position(0, 0), new Velocity(0, 0));

        var worldB = new World();
        worldB.Create<Position>(new Position(0, 0));

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);
        Assert.Single(diff.EntityDiffs);

        var ed = diff.EntityDiffs[0];
        Assert.Equal(EntityDiffKind.Different, ed.Kind);

        var diffForVelocity = ed.ComponentDiffs.Single(d => d.ComponentType == typeof(Velocity));
        Assert.Equal(ComponentDiffKind.OnlyInA, diffForVelocity.Kind);
    }

    [Fact]
    public void Component_only_in_B_produces_component_diff()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(0, 0));

        var worldB = new World();
        worldB.Create<Position, Velocity>(new Position(0, 0), new Velocity(0, 0));

        var diff = WorldDiff.Compare(worldA, worldB);

        var ed = diff.EntityDiffs[0];
        var diffForVelocity = ed.ComponentDiffs.Single(d => d.ComponentType == typeof(Velocity));
        Assert.Equal(ComponentDiffKind.OnlyInB, diffForVelocity.Kind);
    }

    [Fact]
    public void Version_mismatch_with_same_components_still_reports_different()
    {
        var worldA = new World();
        var a1 = worldA.Create<Position>(new Position(10, 20));
        worldA.Destroy(a1);
        worldA.Create<Position>(new Position(10, 20)); // reuses slot 0, version bumped

        var worldB = new World();
        worldB.Create<Position>(new Position(10, 20)); // slot 0, version 1 (never destroyed)

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);
        var ed = diff.EntityDiffs[0];
        Assert.Equal(EntityDiffKind.Different, ed.Kind);
        Assert.True(ed.VersionMismatch);
        Assert.NotEqual(ed.EntityA.Version, ed.EntityB.Version);
        Assert.Empty(ed.ComponentDiffs);
    }

    [Fact]
    public void Hierarchy_diff_detected()
    {
        var worldA = new World();
        var parentA = worldA.Create();
        var childA = worldA.Create();
        worldA.AddChild(parentA, childA);

        var worldB = new World();
        var parentB = worldB.Create();
        var childB = worldB.Create();

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);

        var ed = diff.EntityDiffs.Single(d => d.EntityId == 1);
        Assert.Equal(EntityDiffKind.Different, ed.Kind);
        Assert.NotNull(ed.HierarchyDiff);
        Assert.Equal(0, ed.HierarchyDiff!.ParentIdA);
        Assert.Equal(-1, ed.HierarchyDiff.ParentIdB);
    }

    [Fact]
    public void Different_slot_counts_handled()
    {
        var worldA = new World();
        worldA.Create(); // slot 0
        worldA.Create(); // slot 1

        var worldB = new World();
        worldB.Create();       // slot 0
        var b2 = worldB.Create(); // slot 1
        worldB.Create();       // slot 2
        worldB.Destroy(b2);

        var diff = WorldDiff.Compare(worldA, worldB);

        var slot1 = diff.EntityDiffs.Single(d => d.EntityId == 1);
        Assert.Equal(EntityDiffKind.OnlyInA, slot1.Kind);

        var slot2 = diff.EntityDiffs.Single(d => d.EntityId == 2);
        Assert.Equal(EntityDiffKind.OnlyInB, slot2.Kind);
    }

    [Fact]
    public void Free_list_diff_detected()
    {
        var worldA = new World();
        var a = worldA.Create();
        worldA.Destroy(a);

        var worldB = new World();
        var b = worldB.Create();
        worldB.Destroy(b);

        var diffSame = WorldDiff.Compare(worldA, worldB);
        Assert.Null(diffSame.FreeListDiff);

        var worldC = new World();
        var c1 = worldC.Create();
        var c2 = worldC.Create();
        worldC.Destroy(c2);
        worldC.Destroy(c1);

        var diff = WorldDiff.Compare(worldA, worldC);
        Assert.NotNull(diff.FreeListDiff);
        Assert.NotEmpty(diff.FreeListDiff!.SlotDiffs);
    }

    [Fact]
    public void Same_free_list_span_different_slot_count_reports_free_list_diff()
    {
        // A: slot 0 is free. EntitySlotCount = 1.
        var worldA = new World();
        var a = worldA.Create();
        worldA.Destroy(a);

        // B: slot 0 is free, slot 1 is alive.
        // Same free list span [(0, v1)], but EntitySlotCount = 2.
        var worldB = new World();
        var b0 = worldB.Create();  // slot 0
        worldB.Create();            // slot 1 (EntitySlotCount → 2)
        worldB.Destroy(b0);         // slot 0 free

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.NotNull(diff.FreeListDiff);
        Assert.Equal(1, diff.FreeListDiff!.EntitySlotCountA);
        Assert.Equal(2, diff.FreeListDiff.EntitySlotCountB);
    }

    [Fact]
    public void Free_slot_same_id_different_version_detected()
    {
        var worldA = new World();
        var a = worldA.Create();
        worldA.Destroy(a); // slot 0 free, v1

        var worldB = new World();
        var b1 = worldB.Create();   // slot 0, v1
        worldB.Destroy(b1);
        var b2 = worldB.Create();   // slot 0 reused, v2
        worldB.Destroy(b2);          // slot 0 free, v2

        var diff = WorldDiff.Compare(worldA, worldB);
        Assert.NotNull(diff.FreeListDiff);
        Assert.NotEmpty(diff.FreeListDiff!.SlotDiffs);
        Assert.Equal(FreeSlotDiffKind.VersionMismatch, diff.FreeListDiff.SlotDiffs[0].Kind);
    }

    [Fact]
    public void Multiple_archetypes_all_compared()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(10, 20));
        worldA.Create<Position, Velocity>(new Position(30, 40), new Velocity(1, 2));

        var worldB = new World();
        worldB.Create<Position>(new Position(99, 88));
        worldB.Create<Position, Velocity>(new Position(30, 40), new Velocity(1, 2));

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.False(diff.AreIdentical);
        Assert.Single(diff.EntityDiffs);

        var ed = diff.EntityDiffs[0];
        Assert.Equal(0, ed.EntityId);
        Assert.Equal(EntityDiffKind.Different, ed.Kind);
        Assert.Single(ed.ComponentDiffs);
    }

    [Fact]
    public void Identical_worlds_with_hierarchy_are_identical()
    {
        var worldA = new World();
        var parentA = worldA.Create();
        var childA = worldA.Create();
        worldA.AddChild(parentA, childA);

        var worldB = new World();
        var parentB = worldB.Create();
        var childB = worldB.Create();
        worldB.AddChild(parentB, childB);

        var diff = WorldDiff.Compare(worldA, worldB);
        Assert.True(diff.AreIdentical);
    }

    [Fact]
    public void Both_components_and_hierarchy_diff_reported_together()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(1, 2));
        var parentA = worldA.Create();
        worldA.AddChild(parentA, new Entity(0, 1));

        var worldB = new World();
        worldB.Create<Position>(new Position(99, 100));
        var parentB = worldB.Create();

        var diff = WorldDiff.Compare(worldA, worldB);

        var ed = diff.EntityDiffs.Single(d => d.EntityId == 0);
        Assert.Equal(EntityDiffKind.Different, ed.Kind);
        Assert.Single(ed.ComponentDiffs);
        Assert.Equal(ComponentDiffKind.ValueDifferent, ed.ComponentDiffs[0].Kind);
        Assert.NotNull(ed.HierarchyDiff);
        Assert.Equal(1, ed.HierarchyDiff!.ParentIdA);
        Assert.Equal(-1, ed.HierarchyDiff.ParentIdB);
    }

    [Fact]
    public void Identical_worlds_after_clone_roundtrip_are_identical()
    {
        var worldA = new World();
        worldA.Create<Position, Velocity>(new Position(1, 2), new Velocity(3, 4));
        worldA.Create<Health>(new Health(100));

        var clone = worldA.Clone();

        var diff = WorldDiff.Compare(worldA, clone);
        Assert.True(diff.AreIdentical);
    }

    [Fact]
    public void Empty_worlds_are_identical()
    {
        var diff = WorldDiff.Compare(new World(), new World());
        Assert.True(diff.AreIdentical);
        Assert.Empty(diff.EntityDiffs);
        Assert.Null(diff.FreeListDiff);
    }

    [Fact]
    public void Clone_after_fragmented_destroys_is_still_identical()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(42, 42));
        var temp1 = worldA.Create<Position>(new Position(1, 1));
        var temp2 = worldA.Create<Position>(new Position(2, 2));
        worldA.Create<Velocity>(new Velocity(5, 5));
        worldA.Destroy(temp1);
        worldA.Destroy(temp2);

        var clone = worldA.Clone();

        var diff = WorldDiff.Compare(worldA, clone);
        Assert.True(diff.AreIdentical);
    }

    [Fact]
    public void Chunked_archetype_component_value_diff_detected()
    {
        var worldA = new World(chunkCapacity: 2);
        for (var i = 0; i < 20; i++)
            worldA.Create<Position>(new Position(i, i * 10));

        var worldB = new World(chunkCapacity: 2);
        for (var i = 0; i < 19; i++)
            worldB.Create<Position>(new Position(i, i * 10));
        worldB.Create<Position>(new Position(999, 999));

        var diff = WorldDiff.Compare(worldA, worldB);

        var ed = diff.EntityDiffs.Single(d => d.EntityId == 19);
        Assert.Equal(EntityDiffKind.Different, ed.Kind);
        Assert.Single(ed.ComponentDiffs);
        Assert.Equal(typeof(Position), ed.ComponentDiffs[0].ComponentType);
        Assert.Equal(ComponentDiffKind.ValueDifferent, ed.ComponentDiffs[0].Kind);
    }

    [Fact]
    public void Pending_reservation_throws()
    {
        var world = new World();
        world.ReserveDeferredEntityUnsafe();

        Assert.Throws<InvalidOperationException>(() => WorldDiff.Compare(world, new World()));
    }

    [Fact]
    public void Result_collections_are_readonly_not_mutable_lists()
    {
        var worldA = new World();
        worldA.Create<Position>(new Position(1, 2));

        var worldB = new World();
        worldB.Create<Position>(new Position(99, 100));

        var diff = WorldDiff.Compare(worldA, worldB);

        Assert.IsType<ReadOnlyCollection<EntityDiff>>(diff.EntityDiffs);

        var ed = diff.EntityDiffs[0];
        Assert.IsType<ReadOnlyCollection<ComponentDiff>>(ed.ComponentDiffs);
    }
}
