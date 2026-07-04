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
/// <b>Lifecycle contract:</b>
/// <list type="bullet">
/// <item>A snapshot returned by <see cref="World.CaptureState"/> is owned by
/// the caller until passed to <see cref="World.RestoreState"/>.</item>
/// <item>After <see cref="World.RestoreState"/>, <see cref="IsRecycled"/>
/// becomes <c>true</c> and the snapshot is returned to the world's pool —
/// any subsequent use (including a second <c>RestoreState</c>) will throw
/// <see cref="InvalidOperationException"/>.</item>
/// <item>Multiple snapshots may be live simultaneously, supporting GGPO
/// rollback windows deeper than 1 frame (capture N frames ahead, restore
/// them out of order on misprediction).</item>
/// </list>
/// Correct usage:
/// <code>
/// // Frame loop (zero GC after warmup):
/// var rollback = world.CaptureState();
/// // ... simulate and optionally detect misprediction ...
/// world.RestoreState(rollback);
/// // ... re-simulate with corrected inputs ...
/// </code>
/// Multi-frame rollback window:
/// <code>
/// // Capture several frames ahead (GGPO-style depth &gt; 1):
/// var ring = new WorldStateSnapshot[WindowDepth];
/// for (int i = 0; i &lt; WindowDepth; i++)
/// {
///     ring[i] = world.CaptureState();
///     SimulateOneFrame();
/// }
/// // On misprediction at frame k, restore that frame and re-simulate forward:
/// world.RestoreState(ring[k]);
/// </code>
/// Incorrect usage (use WorldSnapshot instead):
/// <code>
/// // DON'T: serialize a WorldStateSnapshot to bytes
/// // DON'T: send it over the network
/// // DON'T: save it to a replay file
/// // DON'T: call RestoreState twice on the same handle
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

    // Tracks lifecycle state. true when in the world's pool (or freshly
    // constructed and not yet filled), false when handed to a caller via
    // CaptureState. Set to true by RestoreState before returning to the pool.
    // The internal field avoids a property backing field; the public property
    // is the documented API.
    internal bool _isRecycled = true;

    /// <summary>
    /// Gets whether this snapshot has been recycled back to the world's pool.
    /// <c>true</c> after <see cref="World.RestoreState"/> has been called on
    /// this instance (or before it has ever been filled by
    /// <see cref="World.CaptureState"/>). Any operation on a recycled
    /// snapshot other than dropping the reference is undefined behaviour and
    /// will throw on the World APIs.
    /// </summary>
    public bool IsRecycled => _isRecycled;

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
    public int[] ColumnByteOffsets;

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
        Array.Copy(arch.GetEntityStorageUnsafe(), entry.Entities, count);

        var totalBytes = arch.TotalDataBytes;
        if (entry.Data is null || entry.Data.Length < totalBytes)
            entry.Data = new byte[totalBytes];
        arch.CopyDataTo(entry.Data);

        entry.ColumnByteOffsets = arch.ColumnByteOffsets;
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
            ref var seg = ref arch.GetSegmentRef(i);
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
            if (!arch.IsChunked)
            {
                Array.Copy(Entities, arch.GetEntityStorageUnsafe(), Count);
                arch.CopyDataFrom(Data);
                arch.SetCount(Count);
            }
            else
            {
                // Backup was non-chunked but the archetype was promoted to
                // chunked after capture (e.g. prediction created enough
                // entities). Distribute the flat backup across segments,
                // translating from backup-time offsets to segment offsets.
                arch.RestoreFlatBackup(Entities, Data, ColumnByteOffsets, Count);
            }
        }
        else
        {
            arch.ResetCount();
            for (var i = 0; i < SegmentCount; i++)
            {
                ref var seg = ref arch.GetSegmentRef(i);
                // Copy at most the destination capacity — SegmentEntities[i]
                // / SegmentData[i] may be larger than seg.Entities / seg.Data
                // when the backup pool reused an entry from an archetype with
                // larger segments. CopyFromChunked only fills up to
                // seg.Entities.Length / seg.Data.Length, so the extra tail
                // slots contain stale data from a prior capture.
                Array.Copy(SegmentEntities[i], seg.Entities, seg.Entities.Length);
                Array.Copy(SegmentData[i], seg.Data, seg.Data.Length);
                seg.Count = SegmentCounts[i];
            }
            arch.SetCount(Count);
            arch.RebuildFlatEntities();
        }
    }
}
