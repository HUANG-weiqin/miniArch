namespace MiniArch.Core;

/// <summary>
/// Opaque handle to a captured world state for in-memory rollback.
/// Obtain via <see cref="World.CaptureState"/>, restore via
/// <see cref="World.RestoreState"/>. The snapshot is reusable: after
/// RestoreState, the same instance is recycled for the next CaptureState,
/// achieving zero allocation in steady state.
/// </summary>
public sealed class WorldStateSnapshot
{
    internal EntityRecord[] Records = [];
    internal int EntitySlotCount;

    internal World.RecycledEntity[] FreeIds = [];
    internal int FreeIdCount;

    internal Dictionary<Archetype, ArchetypeStateBackup> ArchetypeBackups = new();

    // Hierarchy
    internal Entity[] HierarchyParentByChild = [];
    internal int[] HierarchyFirstChild = [];
    internal Entity[] HierarchyChildEntity = [];
    internal int[] HierarchyChildNext = [];
    internal int HierarchyChildSlotCount;
    internal int HierarchyChildFreeList;
}

internal sealed class ArchetypeStateBackup
{
    internal Entity[] Entities = [];
    internal byte[] Data = [];
    internal int Count;
    internal bool IsChunked;

    // Chunked mode
    internal Entity[][] SegmentEntities = [];
    internal byte[][] SegmentData = [];
    internal int[] SegmentCounts = [];
    internal int SegmentCount;
}
