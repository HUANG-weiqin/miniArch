using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch.Diagnostics;

/// <summary>
/// Compares two <see cref="World"/> instances and produces a structured diff.
/// Primarily used for lockstep divergence diagnosis: after
/// <see cref="World.Checksum"/> detects a mismatch, use <see cref="Compare"/>
/// to pinpoint which entities and components caused it.
/// </summary>
/// <remarks>
/// <b>Quiescent worlds only.</b> Both worlds must be free of pending
/// reservations (outstanding <c>CommandStream.Create</c> calls that have
/// not yet been submitted). Comparing a world with reserved-but-unmaterialised
/// entity slots produces misleading results because the slot allocator state
/// is incomplete. <see cref="Compare"/> detects this condition and throws
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public static class WorldDiff
{
    /// <summary>
    /// Compares two worlds by slot index. Entities are matched by
    /// <see cref="Entity.Id"/> —this is correct for lockstep peers that
    /// replay the same delta sequence. Free lists and hierarchy edges are
    /// included in the comparison.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when either world has pending entity reservations
    /// (reserved slots not yet materialised into an archetype).
    /// Ensure all <c>CommandStream</c> instances have been submitted
    /// before calling this method.
    /// </exception>
    public static WorldDiffResult Compare(World worldA, World worldB)
    {
        ArgumentNullException.ThrowIfNull(worldA);
        ArgumentNullException.ThrowIfNull(worldB);

        AssertNoPendingReservations(worldA);
        AssertNoPendingReservations(worldB);

        var entityDiffs = new List<EntityDiff>();
        var maxSlots = Math.Max(worldA.EntitySlotCount, worldB.EntitySlotCount);

        for (var id = 0; id < maxSlots; id++)
        {
            var aliveA = IsSlotAlive(worldA, id);
            var aliveB = IsSlotAlive(worldB, id);

            if (!aliveA && !aliveB) continue;

            if (aliveA && !aliveB)
            {
                entityDiffs.Add(new EntityDiff(id, EntityDiffKind.OnlyInA,
                    BuildEntityHandle(worldA, id), default));
                continue;
            }

            if (!aliveA && aliveB)
            {
                entityDiffs.Add(new EntityDiff(id, EntityDiffKind.OnlyInB,
                    default, BuildEntityHandle(worldB, id)));
                continue;
            }

            // Both alive — deep compare.
            var recA = GetRecord(worldA, id);
            var recB = GetRecord(worldB, id);
            var verA = recA.Version;
            var verB = recB.Version;

            var componentDiffs = CompareComponents(recA, recB);
            var hierarchyDiff = CompareHierarchy(worldA, worldB, id);

            var hasVersionMismatch = verA != verB;
            var hasAnyDiff = hasVersionMismatch || componentDiffs.Count > 0 || hierarchyDiff is not null;

            if (!hasAnyDiff) continue;

            entityDiffs.Add(new EntityDiff(
                id, EntityDiffKind.Different,
                new Entity(id, verA), new Entity(id, verB),
                hasVersionMismatch,
                componentDiffs,
                hierarchyDiff));
        }

        var freeListDiff = CompareFreeLists(worldA, worldB);

        return new WorldDiffResult(
            new ReadOnlyCollection<EntityDiff>(entityDiffs.ToArray()),
            freeListDiff);
    }

    /// <summary>
    /// Throws if <paramref name="world"/> has any reserved-but-unmaterialised
    /// entity slots. These occur when <c>CommandStream.Create</c> has been
    /// called but the stream has not yet been submitted.
    /// </summary>
    private static void AssertNoPendingReservations(World world)
    {
        // A slot is "reserved" when it is neither occupied (Archetype != null)
        // nor in the free list. The count invariant:
        //   EntitySlotCount == occupiedCount + freeCount + reservedCount
        var occupied = 0;
        var records = world.EntityRecords;
        for (var i = 0; i < world.EntitySlotCount; i++)
        {
            if (records[i].IsOccupied) occupied++;
        }

        var freeCount = world.FreeList.Length;
        var reserved = world.EntitySlotCount - occupied - freeCount;

        if (reserved > 0)
        {
            throw new InvalidOperationException(
                $"World has {reserved} pending entity reservation(s). " +
                "WorldDiff.Compare requires quiescent worlds — ensure all " +
                "CommandStream instances have been submitted before comparing.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSlotAlive(World world, int id)
    {
        if ((uint)id >= (uint)world.EntitySlotCount) return false;
        return world.EntityRecords[id].IsOccupied;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Entity BuildEntityHandle(World world, int id)
    {
        return new Entity(id, world.EntityRecords[id].Version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EntityRecord GetRecord(World world, int id)
    {
        return world.EntityRecords[id];
    }

    private static IReadOnlyList<ComponentDiff> CompareComponents(EntityRecord recA, EntityRecord recB)
    {
        var archA = recA.Archetype!;
        var archB = recB.Archetype!;
        var rowA = recA.RowIndex;
        var rowB = recB.RowIndex;

        // Fast path: same archetype, same signature → compare columns in order.
        if (ReferenceEquals(archA, archB) || archA.Signature.Equals(archB.Signature))
        {
            for (var col = 0; col < archA.Signature.Count; col++)
            {
                var bytesA = archA.GetComponentBytes(col, rowA);
                var bytesB = archB.GetComponentBytes(col, rowB);
                if (!bytesA.SequenceEqual(bytesB))
                    return CompareComponentsFull(archA, archB, rowA, rowB);
            }
            return Array.Empty<ComponentDiff>();
        }

        return CompareComponentsFull(archA, archB, rowA, rowB);
    }

    /// <summary>
    /// Full component comparison via type-union. Only entered when a diff
    /// is already detected (different archetypes or at least one value differs),
    /// so paying the HashSet allocation is justified.
    /// </summary>
    private static List<ComponentDiff> CompareComponentsFull(
        Archetype archA, Archetype archB, int rowA, int rowB)
    {
        var diffs = new List<ComponentDiff>();

        var seen = new HashSet<Type>(archA.ComponentTypes);
        foreach (var t in archB.ComponentTypes) seen.Add(t);

        foreach (var compType in seen)
        {
            var inA = archA.TryGetComponentIndex(
                ComponentRegistry.Shared.GetOrCreate(compType), out var colA);
            var inB = archB.TryGetComponentIndex(
                ComponentRegistry.Shared.GetOrCreate(compType), out var colB);

            if (inA && !inB)
            {
                diffs.Add(new ComponentDiff(compType, ComponentDiffKind.OnlyInA));
            }
            else if (!inA && inB)
            {
                diffs.Add(new ComponentDiff(compType, ComponentDiffKind.OnlyInB));
            }
            else
            {
                var bytesA = archA.GetComponentBytes(colA, rowA);
                var bytesB = archB.GetComponentBytes(colB, rowB);
                if (!bytesA.SequenceEqual(bytesB))
                    diffs.Add(new ComponentDiff(compType, ComponentDiffKind.ValueDifferent));
            }
        }

        return diffs;
    }

    private static HierarchyDiff? CompareHierarchy(World worldA, World worldB, int childId)
    {
        var hasParentA = worldA.Hierarchy.TryGetParent(
            worldA, new Entity(childId, GetRecord(worldA, childId).Version), out var parentA);
        var hasParentB = worldB.Hierarchy.TryGetParent(
            worldB, new Entity(childId, GetRecord(worldB, childId).Version), out var parentB);

        var idA = hasParentA ? parentA.Id : -1;
        var idB = hasParentB ? parentB.Id : -1;

        if (idA == idB) return null;
        return new HierarchyDiff(idA, idB);
    }

    private static FreeListDiff? CompareFreeLists(World worldA, World worldB)
    {
        var freeA = worldA.FreeList;
        var freeB = worldB.FreeList;

        if (freeA.Length == freeB.Length &&
            worldA.EntitySlotCount == worldB.EntitySlotCount &&
            MemoryExtensions.SequenceEqual(freeA, freeB))
        {
            return null;
        }

        var diffs = new List<FreeSlotDiff>();
        var minLen = Math.Min(freeA.Length, freeB.Length);

        for (var i = 0; i < minLen; i++)
        {
            var a = freeA[i];
            var b = freeB[i];
            if (a.Id == b.Id && a.Version == b.Version) continue;

            if (a.Id != b.Id)
                diffs.Add(new FreeSlotDiff(a.Id, FreeSlotDiffKind.OrderMismatch, a.Version, b.Version, b.Id));
            else
                diffs.Add(new FreeSlotDiff(a.Id, FreeSlotDiffKind.VersionMismatch, a.Version, b.Version));
        }

        for (var i = minLen; i < freeA.Length; i++)
            diffs.Add(new FreeSlotDiff(freeA[i].Id, FreeSlotDiffKind.ExtraInA, freeA[i].Version, 0));

        for (var i = minLen; i < freeB.Length; i++)
            diffs.Add(new FreeSlotDiff(freeB[i].Id, FreeSlotDiffKind.ExtraInB, 0, freeB[i].Version));

        return new FreeListDiff(
            freeA.Length, freeB.Length,
            worldA.EntitySlotCount, worldB.EntitySlotCount,
            diffs.ToArray());
    }
}

/// <summary>
/// Result of <see cref="WorldDiff.Compare"/>.
/// </summary>
public sealed class WorldDiffResult
{
    private readonly ReadOnlyCollection<EntityDiff> _entityDiffs;

    internal WorldDiffResult(ReadOnlyCollection<EntityDiff> entityDiffs, FreeListDiff? freeListDiff)
    {
        _entityDiffs = entityDiffs;
        FreeListDiff = freeListDiff;
    }

    /// <summary>
    /// True when entity state, hierarchy, and free lists are identical
    /// between the two worlds.
    /// </summary>
    public bool AreIdentical => _entityDiffs.Count == 0 && FreeListDiff is null;

    /// <summary>Per-entity differences, ordered by entity slot id.</summary>
    public IReadOnlyList<EntityDiff> EntityDiffs => _entityDiffs;

    /// <summary>
    /// Free-list differences, or null when the free lists are identical.
    /// </summary>
    public FreeListDiff? FreeListDiff { get; }
}

/// <summary>
/// How an entity slot differs between two worlds.
/// </summary>
public enum EntityDiffKind
{
    /// <summary>Entity is alive in world A but absent (or free) in world B.</summary>
    OnlyInA,
    /// <summary>Entity is alive in world B but absent (or free) in world A.</summary>
    OnlyInB,
    /// <summary>Entity is alive in both worlds but has different state.</summary>
    Different
}

/// <summary>
/// Describes how a single entity differs between two worlds.
/// </summary>
public sealed class EntityDiff
{
    /// <summary>The entity slot id (same in both worlds for lockstep peers).</summary>
    public int EntityId { get; }

    /// <summary>Kind of difference at the entity level.</summary>
    public EntityDiffKind Kind { get; }

    /// <summary>
    /// The entity handle in world A, or <c>default</c> if the entity is
    /// absent from world A (<see cref="Kind"/> is <see cref="EntityDiffKind.OnlyInB"/>).
    /// </summary>
    public Entity EntityA { get; }

    /// <summary>
    /// The entity handle in world B, or <c>default</c> if the entity is
    /// absent from world B (<see cref="Kind"/> is <see cref="EntityDiffKind.OnlyInA"/>).
    /// </summary>
    public Entity EntityB { get; }

    /// <summary>
    /// True when the entity's version differs between the two worlds.
    /// Only meaningful when <see cref="Kind"/> is <see cref="EntityDiffKind.Different"/>.
    /// </summary>
    public bool VersionMismatch { get; }

    /// <summary>
    /// Per-component differences. Empty when the component sets and values
    /// are identical. Only meaningful when <see cref="Kind"/> is
    /// <see cref="EntityDiffKind.Different"/>.
    /// </summary>
    public IReadOnlyList<ComponentDiff> ComponentDiffs { get; }

    /// <summary>
    /// Hierarchy difference for this entity, or <c>null</c> when the
    /// parent-child relationship is identical. Only meaningful when
    /// <see cref="Kind"/> is <see cref="EntityDiffKind.Different"/>.
    /// </summary>
    public HierarchyDiff? HierarchyDiff { get; }

    internal EntityDiff(
        int entityId, EntityDiffKind kind,
        Entity entityA, Entity entityB,
        bool versionMismatch = false,
        IReadOnlyList<ComponentDiff>? componentDiffs = null,
        HierarchyDiff? hierarchyDiff = null)
    {
        EntityId = entityId;
        Kind = kind;
        EntityA = entityA;
        EntityB = entityB;
        VersionMismatch = versionMismatch;
        ComponentDiffs = FreezeComponentDiffs(componentDiffs);
        HierarchyDiff = hierarchyDiff;
    }

    private static ReadOnlyCollection<ComponentDiff> FreezeComponentDiffs(
        IReadOnlyList<ComponentDiff>? diffs)
    {
        if (diffs is null || diffs.Count == 0)
            return EmptyComponentDiffs;

        if (diffs is List<ComponentDiff> list)
            return Array.AsReadOnly(list.ToArray());

        // Already frozen (Array.Empty, ReadOnlyCollection, or array from fast path).
        if (diffs is ReadOnlyCollection<ComponentDiff> roc) return roc;
        if (diffs is ComponentDiff[] arr) return Array.AsReadOnly(arr);

        return Array.AsReadOnly(diffs.ToArray());
    }

    private static readonly ReadOnlyCollection<ComponentDiff> EmptyComponentDiffs =
        Array.AsReadOnly(Array.Empty<ComponentDiff>());

    /// <summary>Human-readable summary of the difference.</summary>
    public override string ToString()
    {
        return Kind switch
        {
            EntityDiffKind.OnlyInA => $"Entity {EntityId} only in A ({EntityA})",
            EntityDiffKind.OnlyInB => $"Entity {EntityId} only in B ({EntityB})",
            EntityDiffKind.Different => $"Entity {EntityId} differs " +
                $"(v{EntityA.Version} vs v{EntityB.Version}" +
                $"{(VersionMismatch ? " VERSION_MISMATCH" : "")}, " +
                $"{ComponentDiffs.Count} component diffs" +
                $"{(HierarchyDiff is not null ? ", hierarchy" : "")})",
            _ => $"Entity {EntityId}: {Kind}"
        };
    }
}

/// <summary>
/// How a component differs for an entity present in both worlds.
/// </summary>
public enum ComponentDiffKind
{
    /// <summary>Component exists on the entity in world A but not in world B.</summary>
    OnlyInA,
    /// <summary>Component exists on the entity in world B but not in world A.</summary>
    OnlyInB,
    /// <summary>Component exists in both worlds but the raw byte values differ.</summary>
    ValueDifferent
}

/// <summary>
/// Describes how a single component differs for a specific entity.
/// </summary>
public sealed class ComponentDiff
{
    /// <summary>The component's CLR type.</summary>
    public Type ComponentType { get; }

    /// <summary>How the component differs.</summary>
    public ComponentDiffKind Kind { get; }

    internal ComponentDiff(Type componentType, ComponentDiffKind kind)
    {
        ComponentType = componentType;
        Kind = kind;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{ComponentType.Name}: {Kind}";
}

/// <summary>
/// Parent-child difference for an entity that exists in both worlds.
/// </summary>
public sealed class HierarchyDiff
{
    /// <summary>Parent entity id in world A, or -1 if the entity has no parent in A.</summary>
    public int ParentIdA { get; }

    /// <summary>Parent entity id in world B, or -1 if the entity has no parent in B.</summary>
    public int ParentIdB { get; }

    internal HierarchyDiff(int parentIdA, int parentIdB)
    {
        ParentIdA = parentIdA;
        ParentIdB = parentIdB;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Parent: {ParentIdA} (A) vs {ParentIdB} (B)";
}

/// <summary>
/// Summary of free-list differences between two worlds.
/// </summary>
public sealed class FreeListDiff
{
    /// <summary>Number of free slots in world A.</summary>
    public int FreeCountA { get; }

    /// <summary>Number of free slots in world B.</summary>
    public int FreeCountB { get; }

    /// <summary>Total entity slot count in world A.</summary>
    public int EntitySlotCountA { get; }

    /// <summary>Total entity slot count in world B.</summary>
    public int EntitySlotCountB { get; }

    /// <summary>Per-slot free-list differences.</summary>
    public ReadOnlyCollection<FreeSlotDiff> SlotDiffs { get; }

    internal FreeListDiff(
        int freeCountA, int freeCountB,
        int entitySlotCountA, int entitySlotCountB,
        FreeSlotDiff[] slotDiffs)
    {
        FreeCountA = freeCountA;
        FreeCountB = freeCountB;
        EntitySlotCountA = entitySlotCountA;
        EntitySlotCountB = entitySlotCountB;
        SlotDiffs = Array.AsReadOnly(slotDiffs);
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Free list: A={FreeCountA} B={FreeCountB} slots, " +
        $"{SlotDiffs.Count} differences";
}

/// <summary>
/// How a free-list slot differs between two worlds.
/// </summary>
public enum FreeSlotDiffKind
{
    /// <summary>Same slot id in both lists but different version.</summary>
    VersionMismatch,
    /// <summary>Different slot id at the same position (order divergence).</summary>
    OrderMismatch,
    /// <summary>Slot present in A's free list but beyond B's list length.</summary>
    ExtraInA,
    /// <summary>Slot present in B's free list but beyond A's list length.</summary>
    ExtraInB
}

/// <summary>
/// Describes a single free-list slot difference.
/// </summary>
public sealed class FreeSlotDiff
{
    /// <summary>The slot id in world A.</summary>
    public int SlotIdA { get; }

    /// <summary>
    /// The slot id in world B at the same position.
    /// Only meaningful when <see cref="Kind"/> is <see cref="FreeSlotDiffKind.OrderMismatch"/>.
    /// </summary>
    public int SlotIdB { get; }

    /// <summary>How this slot differs.</summary>
    public FreeSlotDiffKind Kind { get; }

    /// <summary>Version of the free slot in world A.</summary>
    public int VersionA { get; }

    /// <summary>Version of the free slot in world B.</summary>
    public int VersionB { get; }

    internal FreeSlotDiff(int slotIdA, FreeSlotDiffKind kind, int versionA, int versionB, int slotIdB = 0)
    {
        SlotIdA = slotIdA;
        SlotIdB = slotIdB;
        Kind = kind;
        VersionA = versionA;
        VersionB = versionB;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        Kind == FreeSlotDiffKind.OrderMismatch
            ? $"Free slot position: A={SlotIdA} v{VersionA} vs B={SlotIdB} v{VersionB}"
            : $"Slot {SlotIdA} {Kind}: v{VersionA} (A) vs v{VersionB} (B)";
}
