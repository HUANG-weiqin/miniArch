using MiniArch.Core;

namespace MiniArch.Diagnostics;

/// <summary>
/// Static validator that checks a <see cref="World"/> for structural
/// invariant violations: entity slot consistency, free-list integrity,
/// hierarchy bidirectionality, and archetype-level invariants.
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
    [ThreadStatic] private static HashSet<int>? _cycleChainVisited;

    /// <summary>
    /// Runs all validation checks on <paramref name="world"/> and returns
    /// a report of any issues found.
    /// </summary>
    public static ValidationResult Validate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var issues = _issues ??= [];
        issues.Clear();
        (_usedSlots ??= []).Clear();
        (_freeSeen ??= []).Clear();
        (_archSeen ??= []).Clear();

        CheckEntitySlots(world, issues);
        CheckFreeList(world, issues);
        CheckHierarchy(world, issues);
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
            var recycled = freeList[i];

            // Free-list entry is still occupied.
            if ((uint)recycled.Id < (uint)records.Length && records[recycled.Id].IsOccupied)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.FreeListOccupied,
                    $"Recycled entity {recycled.Id} (v{recycled.Version}) is still occupied."));
            }

            // Duplicate ID in free list.
            if (!seenIds.Add(recycled.Id))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCategory.FreeList,
                    ValidationCode.FreeListDuplicate,
                    $"Duplicate recycled entity ID {recycled.Id} in free list."));
            }
        }
    }

    private static void CheckHierarchy(World world, List<ValidationIssue> issues)
    {
        var hierarchy = world.Hierarchy;
        var records = world.EntityRecords;

        foreach (var (child, parent) in hierarchy.EnumerateLiveRelations(world))
        {
            // Parent must exist.
            if ((uint)parent.Id >= (uint)records.Length || !records[parent.Id].IsOccupied)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                    ValidationCode.OrphanedChild,
                    $"Child {child} references dead/missing parent {parent}."));
            }

            // Child must exist.
            if ((uint)child.Id >= (uint)records.Length || !records[child.Id].IsOccupied)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                    ValidationCode.OrphanedChild,
                    $"Child {child} (parent {parent}) is not alive."));
            }

            // Bidirectional: child -> TryGetParent should return the same parent.
            if (hierarchy.TryGetParent(world, child, out var actualParent))
            {
                if (actualParent.Id != parent.Id || actualParent.Version != parent.Version)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                        ValidationCode.AsymmetricParent,
                        $"Child {child} recorded parent {parent} but TryGetParent returns {actualParent}."));
                }
            }
            else
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                    ValidationCode.AsymmetricParent,
                    $"Child {child} recorded parent {parent} but TryGetParent finds none."));
            }
        }

        // Cycle detection: walk parent chain from each live child.
        var chainVisited = _cycleChainVisited ??= [];
        chainVisited.Clear();

        foreach (var (child, _) in hierarchy.EnumerateLiveRelations(world))
        {
            chainVisited.Clear();
            var current = child;

            while (true)
            {
                if (!chainVisited.Add(current.Id))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                        ValidationCode.HierarchyCycle,
                        $"Hierarchy cycle detected: entity {current} appears twice in the parent chain starting from {child}."));
                    break;
                }

                if (!hierarchy.TryGetParent(world, current, out var next))
                {
                    break; // Reached root (no parent).
                }

                current = next;
            }
        }
    }

    private static void CheckArchetypes(World world, List<ValidationIssue> issues)
    {
        var seenEntityIds = _archSeen!;

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
                if (!seenEntityIds.Add(entities[i].Id))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Archetype,
                        ValidationCode.DuplicateEntityId,
                        $"Entity {entities[i]} appears in multiple archetypes."));
                }
            }
        }

        // Slot capacity warning.
        var occupiedCount = 0;
        var records = world.EntityRecords;
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i].IsOccupied) occupiedCount++;
        }
        var totalKnown = occupiedCount + world.FreeList.Length;
        if (totalKnown < world.EntitySlotCount)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, ValidationCategory.Archetype,
                ValidationCode.SlotCapacityWarning,
                $"EntitySlotCount={world.EntitySlotCount} > occupied({occupiedCount}) + free({world.FreeList.Length}) = {totalKnown}. Possible pending reservations."));
        }
        else if (totalKnown > world.EntitySlotCount)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Archetype,
                ValidationCode.SlotCapacityWarning,
                $"EntitySlotCount={world.EntitySlotCount} < occupied({occupiedCount}) + free({world.FreeList.Length}) = {totalKnown}. Free-list or slot tracking is corrupted."));
        }
    }
}
