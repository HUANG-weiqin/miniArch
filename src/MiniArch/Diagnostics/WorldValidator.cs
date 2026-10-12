using MiniArch.Core;

namespace MiniArch.Diagnostics;

/// <summary>
/// Static validator that checks a <see cref="World"/> for structural
/// invariant violations: entity slot consistency, free-list integrity,
/// and archetype-level invariants.
/// </summary>
/// <remarks>
/// Zero side-effects; all checks are read-only. Use in debug builds or
/// tests to catch corruption close to the mutation that caused it.
/// </remarks>
public static class WorldValidator
{
    [ThreadStatic] private static List<ValidationIssue>? _issues;
    [ThreadStatic] private static HashSet<(Archetype Archetype, int Row)>? _usedSlots;
    [ThreadStatic] private static HashSet<int>? _freeSeen;
    [ThreadStatic] private static HashSet<int>? _archSeen;

    /// <summary>
    /// Runs all validation checks on <paramref name="world"/> and returns
    /// a report of any issues found.
    /// </summary>
    public static ValidationResult Validate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.AssertNotDisposed();

        var issues = _issues ??= [];
        issues.Clear();
        (_usedSlots ??= []).Clear();
        (_freeSeen ??= []).Clear();
        (_archSeen ??= []).Clear();

        CheckEntitySlots(world, issues);
        CheckFreeList(world, issues);
        CheckArchetypes(world, issues);

        return new ValidationResult(issues);
    }

    private static void CheckEntitySlots(World world, List<ValidationIssue> issues)
    {
        var records = world.EntityRecords;
        var usedSlots = _usedSlots!;

        for (var id = 0; id < records.Length; id++)
        {
            var rec = records[id];
            if (!rec.IsOccupied)
                continue;

            // Orphaned slot: Archetype is null or RowIndex out of range.
            if (rec.Archetype is null || rec.RowIndex < 0 || rec.RowIndex >= rec.Archetype.EntityCount)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.EntitySlot,
                    ValidationCode.OrphanedSlot,
                    $"Slot {id} (v{rec.Version}) references invalid archetype/row: arch={rec.Archetype?.GetHashCode()}, row={rec.RowIndex}, arch.EntityCount={rec.Archetype?.EntityCount ?? 0}."));
                continue;
            }

            var storedEntity = rec.Archetype.GetEntities()[rec.RowIndex];
            if (storedEntity.Id != id || storedEntity.Version != rec.Version)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.EntitySlot,
                    ValidationCode.OrphanedSlot,
                    $"Slot {id} (v{rec.Version}) points to row {rec.RowIndex}, which stores {storedEntity}."));
            }

            // Slot collision: two occupied records share the same (archetype, row).
            if (!usedSlots.Add((rec.Archetype, rec.RowIndex)))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.EntitySlot,
                    ValidationCode.SlotCollision,
                    $"Slot {id} collides on (arch={rec.Archetype.GetHashCode()}, row={rec.RowIndex})."));
            }
        }
    }

    private static void CheckFreeList(World world, List<ValidationIssue> issues)
    {
        var freeList = world.FreeList;
        var records = world.EntityRecords;
        var seenIds = _freeSeen!;

        for (var i = 0; i < freeList.Length; i++)
        {
            var id = freeList[i];

            if ((uint)id >= (uint)world.EntitySlotCount)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.OrphanedSlot,
                    $"Recycled entity {id} has no entity slot."));
            }
            else if (records[id].IsOccupied)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.FreeListOccupied,
                    $"Recycled entity {id} (v{records[id].Version}) is still occupied."));
            }
            else if (records[id].Version <= 0)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.OrphanedSlot,
                    $"Recycled entity {id} has invalid slot version {records[id].Version}."));
            }

            // Duplicate ID in free list.
            if (!seenIds.Add(id))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.FreeListDuplicate,
                    $"Duplicate recycled entity ID {id} in free list."));
            }
        }
    }

    private static void CheckArchetypes(World world, List<ValidationIssue> issues)
    {
        var seenEntityIds = _archSeen!;
        var records = world.EntityRecords;

        foreach (var arch in world.Archetypes)
        {
            if (arch is null) continue;
            var entities = arch.GetEntities();

            // EntityCount vs actual array length.
            if (arch.EntityCount != entities.Length)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Archetype,
                    ValidationCode.ArchetypeEntityCount,
                    $"Archetype sig={arch.Signature}: EntityCount={arch.EntityCount} but GetEntities length={entities.Length}."));
            }

            // Duplicate entity IDs across archetypes.
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (!seenEntityIds.Add(entity.Id))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Archetype,
                        ValidationCode.DuplicateEntityId,
                        $"Entity {entity} appears in multiple archetypes."));
                }

                if ((uint)entity.Id >= (uint)records.Length)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.EntitySlot,
                        ValidationCode.OrphanedSlot,
                        $"Archetype sig={arch.Signature} row {i} stores {entity}, which has no entity slot."));
                    continue;
                }

                var record = records[entity.Id];
                if (!record.IsOccupied || record.Version != entity.Version ||
                    !ReferenceEquals(record.Archetype, arch) || record.RowIndex != i)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.EntitySlot,
                        ValidationCode.OrphanedSlot,
                        $"Archetype sig={arch.Signature} row {i} stores {entity}, but its entity slot points elsewhere."));
                }
            }
        }

        // Slot capacity warning.
        var occupiedCount = 0;
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i].IsOccupied) occupiedCount++;
        }
        var totalKnown = occupiedCount + world.FreeList.Length;
        var derivedReservedCount = world.EntitySlotCount - totalKnown;
        if (derivedReservedCount < 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Archetype,
                ValidationCode.SlotCapacityWarning,
                $"EntitySlotCount={world.EntitySlotCount} < occupied({occupiedCount}) + free({world.FreeList.Length}) = {totalKnown}. Free-list or slot tracking is corrupted."));
        }
        else if (derivedReservedCount != world.ReservedCount)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Archetype,
                ValidationCode.SlotCapacityWarning,
                $"ReservedCount={world.ReservedCount} does not match slot-derived reservation count {derivedReservedCount}."));
        }
        else if (derivedReservedCount > 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCategory.Archetype,
                ValidationCode.SlotCapacityWarning,
                $"World has {derivedReservedCount} pending reservation(s)."));
        }
    }
}
