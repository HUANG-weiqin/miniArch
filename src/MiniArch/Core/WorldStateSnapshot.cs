namespace MiniArch.Core;

/// <summary>
/// Opaque handle to a captured world state for <b>in-memory rollback only</b>.
/// Obtain via <see cref="World.CaptureState"/>, restore via
/// <see cref="World.RestoreState"/>. After restore the snapshot is recycled
/// for the next capture, achieving <b>zero allocation in steady state</b>.
/// <para/>
/// Designed for GGPO-style frame rollback (60fps save/restore cycles at
/// &lt;1000 entities). <b>NOT</b> for persistence or cross-process use:
/// it contains raw internal arrays (<c>EntityRecord[]</c>, archetype pointers,
/// hierarchy arrays) that are tied to the current process memory layout.
/// <para/>
/// For persistence, checksums, or networking, use <see cref="WorldSnapshot"/>
/// (<c>WorldSnapshot.Save</c> / <c>WorldSnapshot.Load</c>) instead, which
/// encodes to a versioned byte stream suitable for file/network transfer.
/// </summary>
/// <remarks>
/// Correct usage:
/// <code>
/// // Frame loop (zero GC after warmup):
/// var rollback = world.CaptureState();
/// // ... simulate and optionally detect misprediction ...
/// world.RestoreState(rollback);
/// // ... re-simulate with corrected inputs ...
/// </code>
/// Incorrect usage (use WorldSnapshot instead):
/// <code>
/// // DON'T: serialize a WorldStateSnapshot to bytes
/// // DON'T: send it over the network
/// // DON'T: save it to a replay file
/// </code>
/// </remarks>
public sealed class WorldStateSnapshot
{
    internal EntityRecord[] Records = [];
    internal int EntitySlotCount;

    internal int[] FreeIds = [];
    internal int[] FreeIdVersions = [];
    internal int FreeIdCount;

    internal ArchetypeBackupEntry[] ArchetypeBackups = [];
    internal int ArchetypeBackupCount;

    internal Entity[] HierarchyParentByChild = [];
    internal int[] HierarchyFirstChild = [];
    internal Entity[] HierarchyChildEntity = [];
    internal int[] HierarchyChildNext = [];
    internal int HierarchyChildSlotCount;
    internal int HierarchyChildFreeList;

    internal void Clear()
    {
        EntitySlotCount = 0;
        FreeIdCount = 0;
        ArchetypeBackupCount = 0;
        HierarchyChildSlotCount = 0;
        HierarchyChildFreeList = -1;
    }

    internal void EnsureRecordsCapacity(int capacity)
    {
        if (Records.Length < capacity)
            Array.Resize(ref Records, capacity);
    }

    internal void EnsureFreeIdsCapacity(int capacity)
    {
        if (FreeIds.Length < capacity)
            Array.Resize(ref FreeIds, capacity);
        if (FreeIdVersions.Length < capacity)
            Array.Resize(ref FreeIdVersions, capacity);
    }

    internal void EnsureArchetypeBackupsCapacity(int capacity)
    {
        if (ArchetypeBackups.Length < capacity)
            Array.Resize(ref ArchetypeBackups, capacity);
    }

    internal void EnsureHierarchyCapacity(int parentByChildLength, int childSlotCapacity)
    {
        if (HierarchyParentByChild.Length < parentByChildLength)
            Array.Resize(ref HierarchyParentByChild, parentByChildLength);
        if (HierarchyFirstChild.Length < parentByChildLength)
            Array.Resize(ref HierarchyFirstChild, parentByChildLength);
        if (HierarchyChildEntity.Length < childSlotCapacity)
            Array.Resize(ref HierarchyChildEntity, childSlotCapacity);
        if (HierarchyChildNext.Length < childSlotCapacity)
            Array.Resize(ref HierarchyChildNext, childSlotCapacity);
    }
}

internal struct ArchetypeBackupEntry
{
    public Archetype Archetype;
    public Entity[] Entities;
    public byte[] Data;
    public int Count;
    public bool IsChunked;

    public Entity[][] SegmentEntities;
    public byte[][] SegmentData;
    public int[] SegmentCounts;
    public int SegmentCount;

    public static void CopyFromNonChunked(Archetype arch, ref ArchetypeBackupEntry entry)
    {
        var count = arch.EntityCount;
        entry.Archetype = arch;
        entry.Count = count;
        entry.IsChunked = false;

        if (entry.Entities is null || entry.Entities.Length < count)
            entry.Entities = new Entity[count];
        Array.Copy(arch.GetEntityStorage(), entry.Entities, count);

        var totalBytes = arch.TotalDataBytes;
        if (entry.Data is null || entry.Data.Length < totalBytes)
            entry.Data = new byte[totalBytes];
        arch.CopyDataTo(entry.Data);

        entry.SegmentCount = 0;
    }

    public static void CopyFromChunked(Archetype arch, ref ArchetypeBackupEntry entry)
    {
        var segCount = arch.SegmentCount;
        entry.Archetype = arch;
        entry.Count = arch.EntityCount;
        entry.IsChunked = true;
        entry.SegmentCount = segCount;

        if (entry.SegmentEntities is null || entry.SegmentEntities.Length < segCount)
            entry.SegmentEntities = new Entity[segCount][];
        if (entry.SegmentData is null || entry.SegmentData.Length < segCount)
            entry.SegmentData = new byte[segCount][];
        if (entry.SegmentCounts is null || entry.SegmentCounts.Length < segCount)
            entry.SegmentCounts = new int[segCount];

        for (var i = 0; i < segCount; i++)
        {
            var seg = arch.GetSegment(i);
            if (entry.SegmentEntities[i] is null || entry.SegmentEntities[i].Length < seg.Entities.Length)
                entry.SegmentEntities[i] = new Entity[seg.Entities.Length];
            Array.Copy(seg.Entities, entry.SegmentEntities[i], seg.Entities.Length);
            if (entry.SegmentData[i] is null || entry.SegmentData[i].Length < seg.Data.Length)
                entry.SegmentData[i] = new byte[seg.Data.Length];
            Array.Copy(seg.Data, entry.SegmentData[i], seg.Data.Length);
            entry.SegmentCounts[i] = seg.Count;
        }

        entry.Entities = null!;
        entry.Data = null!;
    }

    public readonly void RestoreTo(Archetype arch)
    {
        if (!IsChunked)
        {
            Array.Copy(Entities, arch.GetEntityStorage(), Count);
            arch.CopyDataFrom(Data);
            arch.SetCount(Count);
        }
        else
        {
            arch.SetCount(0);
            for (var i = 0; i < SegmentCount; i++)
            {
                ref var seg = ref arch.GetSegmentRef(i);
                Array.Copy(SegmentEntities[i], seg.Entities, SegmentEntities[i].Length);
                Array.Copy(SegmentData[i], seg.Data, SegmentData[i].Length);
                seg.Count = SegmentCounts[i];
            }
            arch.SetCount(Count);
            arch.RebuildFlatEntities();
        }
    }
}
