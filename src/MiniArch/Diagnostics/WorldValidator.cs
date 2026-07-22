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
    [ThreadStatic] private static HashSet<(Entity Parent, Entity Child)>? _forwardRelations;
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
        (_forwardRelations ??= []).Clear();

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
            var recycled = freeList[i];

            if ((uint)recycled.Id >= (uint)records.Length)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.OrphanedSlot,
                    $"Recycled entity {recycled.Id} (v{recycled.Version}) has no entity slot."));
            }
            else if (records[recycled.Id].IsOccupied)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.FreeListOccupied,
                    $"Recycled entity {recycled.Id} (v{recycled.Version}) is still occupied."));
            }
            else if (recycled.Version <= 0 || records[recycled.Id].Version != recycled.Version)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.OrphanedSlot,
                    $"Recycled entity {recycled.Id} version {recycled.Version} does not match slot version {records[recycled.Id].Version}."));
            }

            // Duplicate ID in free list.
            if (!seenIds.Add(recycled.Id))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.FreeList,
                    ValidationCode.FreeListDuplicate,
                    $"Duplicate recycled entity ID {recycled.Id} in free list."));
            }
        }
    }

    private static void CheckHierarchy(World world, List<ValidationIssue> issues)
    {
        var hierarchy = world.Hierarchy;
        var records = world.EntityRecords;

        var forwardRelations = _forwardRelations!;

        // Check the forward direction first and index every live relation. Each
        // live child must point back to the parent whose list contains it.
        // Dead/stale children are intentionally
        // ignored because bulk Clear may leave version-invalid forward entries.
        var firstChildSlots = hierarchy.FirstChildSlots;
        var childSlots = hierarchy.ChildSlots;
        for (var parentId = 0; parentId < firstChildSlots.Length; parentId++)
        {
            var slot = firstChildSlots[parentId];
            var remaining = childSlots.Length;
            while (slot >= 0)
            {
                if ((uint)slot >= (uint)childSlots.Length || remaining-- == 0)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                        ValidationCode.AsymmetricParent,
                        $"Parent slot {parentId} has an invalid or cyclic child-slot chain at index {slot}."));
                    break;
                }

                var childSlot = childSlots[slot];
                var child = childSlot.Entity;
                if (world.IsAlive(child))
                {
                    if ((uint)parentId >= (uint)records.Length || !records[parentId].IsOccupied)
                    {
                        issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                            ValidationCode.OrphanedChild,
                            $"Live child {child} appears in the child list of dead/missing parent slot {parentId}."));
                    }
                    else
                    {
                        var parent = new Entity(parentId, records[parentId].Version);
                        if (!forwardRelations.Add((parent, child)))
                        {
                            issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                                ValidationCode.AsymmetricParent,
                                $"Parent {parent} lists live child {child} more than once."));
                            break;
                        }

                        if (!hierarchy.TryGetParent(world, child, out var actualParent) || actualParent != parent)
                        {
                            issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                                ValidationCode.AsymmetricParent,
                                $"Parent {parent} lists child {child}, but the child points to {(actualParent == default ? "no live parent" : actualParent.ToString())}."));
                        }
                    }
                }

                slot = childSlot.Next;
            }
        }

        // Check the reverse direction against the independently-built forward index.
        foreach (var (child, parent) in hierarchy.EnumerateLiveRelations(world))
        {
            if (!forwardRelations.Contains((parent, child)))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, ValidationCategory.Hierarchy,
                    ValidationCode.AsymmetricParent,
                    $"Child {child} records parent {parent}, but the parent's child list does not contain it."));
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
