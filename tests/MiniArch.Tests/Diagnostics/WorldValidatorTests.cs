using System.Reflection;
using MiniArch.Core;
using MiniArch.Diagnostics;

namespace MiniArchTests.Diagnostics;

file readonly record struct Position(int X, int Y);
file readonly record struct Velocity(int X, int Y, int Z);

public class WorldValidatorTests
{
    [Fact]
    public void EmptyWorld_IsValid()
    {
        using var world = new World();
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void BUG_ValidationResult_keeps_its_issues_after_later_validation()
    {
        using var world = new World();
        var stream = new CommandStream(world);
        stream.Create();

        var withReservation = WorldValidator.Validate(world);
        Assert.False(withReservation.IsValid);
        var reservationIssue = Assert.Single(withReservation.Issues);
        Assert.Equal(ValidationSeverity.Warning, reservationIssue.Severity);

        stream.Clear();
        var quiescent = WorldValidator.Validate(world);

        Assert.True(quiescent.IsValid);
        Assert.Single(withReservation.Issues);
        Assert.Equal(ValidationCode.SlotCapacityWarning, withReservation.Issues[0].Code);
    }

    [Fact]
    public void WorldWithEntities_NoIssues()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Velocity(4, 5, 6));
        world.Create(new Position(7, 8), new Velocity(10, 11, 12));
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyAndRecreate_NoFalsePositives()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);
        world.Create(new Position(7, 8));
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void WorldWithHierarchy_NoIssues()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child = world.Create(new Position(1, 1));
        world.AddChild(parent, child);
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void MultipleArchetypes_NoIssues()
    {
        using var world = new World();
        for (var i = 0; i < 10; i++)
        {
            world.Create(new Position(i, 0));
            if (i % 2 == 0)
                world.Create(new Velocity(i, 0, 0));
        }
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyMany_NoIssues()
    {
        using var world = new World();
        var entities = new List<Entity>();
        for (var i = 0; i < 100; i++)
            entities.Add(world.Create(new Position(i, 0)));
        foreach (var e in entities)
            world.Destroy(e);
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyThenRecreateMany_NoIssues()
    {
        using var world = new World();
        var batch1 = new List<Entity>();
        for (var i = 0; i < 50; i++)
            batch1.Add(world.Create(new Position(i, 0)));
        foreach (var e in batch1)
            world.Destroy(e);

        for (var i = 0; i < 50; i++)
            world.Create(new Position(i * 10, 0));

        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DestroyedEntity_NotInFreeList_FreeListCheckPasses()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Create(new Velocity(4, 5, 6));
        world.Destroy(e);
        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void BUG_WorldValidator_detects_out_of_range_free_list_entry()
    {
        using var world = new World();
        var entity = world.Create(new Position(1, 1));
        world.Destroy(entity);
        var snapshot = world.CaptureState();
        snapshot.FreeEntities[0] = new FreeEntityEntry(99, entity.Version + 1);
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.OrphanedSlot);
    }

    [Fact]
    public void BUG_WorldValidator_detects_free_list_version_mismatch()
    {
        using var world = new World();
        var entity = world.Create(new Position(1, 1));
        world.Destroy(entity);
        var snapshot = world.CaptureState();
        snapshot.FreeEntities[0] = new FreeEntityEntry(entity.Id, entity.Version + 2);
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.OrphanedSlot);
    }

    [Fact]
    public void BUG_WorldValidator_classifies_duplicate_free_id_as_error()
    {
        using var world = new World();
        var first = world.Create(new Position(1, 1));
        var second = world.Create(new Position(2, 2));
        world.Destroy(first);
        world.Destroy(second);
        var snapshot = world.CaptureState();
        snapshot.FreeEntities[1] = snapshot.FreeEntities[0];
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        var issue = Assert.Single(result.Issues, issue => issue.Code == ValidationCode.FreeListDuplicate);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void BUG_WorldValidator_detects_reserved_count_mismatch()
    {
        using var world = new World();
        var stream = new CommandStream(world);
        stream.Create();
        typeof(World).GetField("_reservedCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(world, 0);

        var result = WorldValidator.Validate(world);

        var issue = Assert.Single(result.Issues, issue => issue.Code == ValidationCode.SlotCapacityWarning);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void BUG_WorldValidator_detects_missing_forward_hierarchy_link()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child = world.Create(new Position(1, 1));

        // Inject only the child-to-parent half of the relation.
        world.Hierarchy.SetParentForTest(child, parent);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.AsymmetricParent);
    }

    [Fact]
    public void BUG_WorldValidator_detects_forward_hierarchy_link_to_wrong_parent()
    {
        using var world = new World();
        var firstParent = world.Create(new Position(0, 0));
        var secondParent = world.Create(new Position(1, 1));
        var child = world.Create(new Position(2, 2));
        world.AddChild(firstParent, child);

        // Leave the child in firstParent's forward list but redirect its reverse link.
        world.Hierarchy.SetParentForTest(child, secondParent);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.AsymmetricParent);
    }

    [Fact]
    public void BUG_WorldValidator_detects_cyclic_child_slot_chain()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child = world.Create(new Position(1, 1));
        world.AddChild(parent, child);
        var snapshot = world.CaptureState();
        var childSlot = snapshot.HierarchyFirstChild[parent.Id];
        snapshot.HierarchyChildSlots[childSlot].Next = childSlot;
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.AsymmetricParent);
    }

    [Fact]
    public void BUG_WorldValidator_detects_record_to_archetype_row_mismatch()
    {
        using var world = new World();
        var first = world.Create(new Position(1, 1));
        var second = world.Create(new Position(2, 2));
        var snapshot = world.CaptureState();

        (snapshot.Records[first.Id].RowIndex, snapshot.Records[second.Id].RowIndex) =
            (snapshot.Records[second.Id].RowIndex, snapshot.Records[first.Id].RowIndex);
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.OrphanedSlot);
    }

    [Fact]
    public void Bulk_clear_stale_hierarchy_entries_are_not_reported_as_live_asymmetry()
    {
        using var world = new World();
        var removedParent = world.Create(new Position(0, 0));
        var survivingChild = world.Create(new Velocity(1, 1, 1));
        world.AddChild(removedParent, survivingChild);

        var survivingParent = world.Create(new Velocity(2, 2, 2));
        var removedChild = world.Create(new Position(3, 3));
        world.AddChild(survivingParent, removedChild);

        var description = new QueryDescription().With<Position>();
        world.Clear(in description);

        var result = WorldValidator.Validate(world);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void HierarchyWithCycle_ReportsError()
    {
        using var world = new World();
        var a = world.Create(new Position(0, 0));
        var b = world.Create(new Position(1, 1));
        var c = world.Create(new Position(2, 2));

        // Inject a 3-cycle A -> B -> C -> A via test hook.
        world.Hierarchy.SetParentForTest(b, a);
        world.Hierarchy.SetParentForTest(c, b);
        world.Hierarchy.SetParentForTest(a, c);

        var result = WorldValidator.Validate(world);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ValidationCode.HierarchyCycle);
    }

    [Fact]
    public void HierarchyWithSelfCycle_ReportsError()
    {
        using var world = new World();
        var a = world.Create(new Position(0, 0));

        // Inject self-cycle A -> A.
        world.Hierarchy.SetParentForTest(a, a);

        var result = WorldValidator.Validate(world);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == ValidationCode.HierarchyCycle);
    }

    [Fact]
    public void HierarchyWithoutCycle_NoCycleError()
    {
        using var world = new World();
        var parent = world.Create(new Position(0, 0));
        var child = world.Create(new Position(1, 1));
        var grandchild = world.Create(new Position(2, 2));

        world.AddChild(parent, child);
        world.AddChild(child, grandchild);

        var result = WorldValidator.Validate(world);
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, i => i.Code == ValidationCode.HierarchyCycle);
    }
}
