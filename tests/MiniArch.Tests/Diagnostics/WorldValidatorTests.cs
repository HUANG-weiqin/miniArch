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
        snapshot.Payload.FreeIds[0] = 99;
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.OrphanedSlot);
    }

    [Fact]
    public void BUG_WorldValidator_detects_invalid_free_slot_version()
    {
        using var world = new World();
        var entity = world.Create(new Position(1, 1));
        world.Destroy(entity);
        var snapshot = world.CaptureState();
        snapshot.Payload.Records[entity.Id].Version = 0;
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
        snapshot.Payload.FreeIds[1] = snapshot.Payload.FreeIds[0];
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
    public void BUG_WorldValidator_detects_record_to_archetype_row_mismatch()
    {
        using var world = new World();
        var first = world.Create(new Position(1, 1));
        var second = world.Create(new Position(2, 2));
        var snapshot = world.CaptureState();

        var payload = snapshot.Payload;
        (payload.Records[first.Id].RowIndex, payload.Records[second.Id].RowIndex) =
            (payload.Records[second.Id].RowIndex, payload.Records[first.Id].RowIndex);
        world.RestoreState(snapshot);

        var result = WorldValidator.Validate(world);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ValidationCode.OrphanedSlot);
    }

}
