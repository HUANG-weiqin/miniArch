using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    // Source layout snapshot (deep-copied, not a reference). RestoreTo uses
    // these to interpret the backup data independently of the archetype's
    // current layout, which may have changed during prediction.
    //
    // Non-chunked: SourceCapacity = arch.Capacity at capture time;
    //              ColumnByteOffsets is the flat buffer layout for that capacity.
    // Chunked:     SourceCapacity = 0 (sentinel — not used for chunked backups);
    //              ColumnByteOffsets is the segment layout (= segment capacity).
    public int SourceCapacity;
    public int[] ColumnByteOffsets;

    // Chunked backup: per-segment arrays. SegmentEntities[i] and
    // SegmentData[i] are sized to the source archetype's _segmentCapacity
    // at capture time. Pool reuse may leave them larger than needed, but
    // CopyFromChunked guarantees they are at least as large as necessary.
    public Entity[][] SegmentEntities;
    public byte[][] SegmentData;
    public int[] SegmentCounts;
    public int SegmentCount;

    /// <summary>
    /// Minimum capacity for pooled backup arrays. Prevents pathological
    /// allocation for very small archetypes while keeping steady-state
    /// resize cost negligible.
    /// </summary>
    private const int MinBackupCapacity = 64;

    public static void CopyFromNonChunked(Archetype arch, ref ArchetypeBackupEntry entry)
    {
        var count = arch.EntityCount;
        entry.Archetype = arch;
        entry.Count = count;
        entry.IsChunked = false;
        entry.SourceCapacity = arch.Capacity;
        entry.SegmentCount = 0;

        // Entities
        var neededEntityLen = Math.Max(count, MinBackupCapacity);
        if (entry.Entities is null || entry.Entities.Length < neededEntityLen)
            entry.Entities = new Entity[neededEntityLen];
        Array.Copy(arch.GetEntityStorageUnsafe(), entry.Entities, count);

        // Data (flat buffer)
        var totalBytes = arch.TotalDataBytes;
        if (entry.Data is null || entry.Data.Length < totalBytes)
            entry.Data = new byte[Math.Max(totalBytes, MinBackupCapacity * 16)];
        arch.CopyDataTo(entry.Data);

        // Column offsets: deep copy (not reference) so restore can translate
        // even if the archetype's offsets changed after capture.
        var srcOffsets = arch.ColumnByteOffsets;
        if (entry.ColumnByteOffsets is null || entry.ColumnByteOffsets.Length != srcOffsets.Length)
            entry.ColumnByteOffsets = new int[srcOffsets.Length];
        Array.Copy(srcOffsets, entry.ColumnByteOffsets, srcOffsets.Length);
    }

    public static void CopyFromChunked(Archetype arch, ref ArchetypeBackupEntry entry)
    {
        var segCount = arch.SegmentCount;
        entry.Archetype = arch;
        entry.Count = arch.EntityCount;
        entry.IsChunked = true;
        entry.SourceCapacity = 0; // chunked sentinel
        entry.SegmentCount = segCount;

        var segCap = arch.SegmentCapacity;

        if (entry.SegmentEntities is null || entry.SegmentEntities.Length < segCount)
            entry.SegmentEntities = new Entity[segCount][];
        if (entry.SegmentData is null || entry.SegmentData.Length < segCount)
            entry.SegmentData = new byte[segCount][];
        if (entry.SegmentCounts is null || entry.SegmentCounts.Length < segCount)
            entry.SegmentCounts = new int[segCount];

        for (var i = 0; i < segCount; i++)
        {
            ref var seg = ref arch.GetSegmentRef(i);
            Debug.Assert(seg.Entities.Length == segCap,
                "All segments in a chunked archetype share the same _segmentCapacity.");

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

        // Deep-copy column byte offsets (not used by chunked→chunked restore,
        // but stored for uniformity and so non-chunked→chunked restore via
        // RestoreFlatBackup works if the backup type is chunked).
        var srcOffsets = arch.ColumnByteOffsets;
        if (entry.ColumnByteOffsets is null || entry.ColumnByteOffsets.Length != srcOffsets.Length)
            entry.ColumnByteOffsets = new int[srcOffsets.Length];
        Array.Copy(srcOffsets, entry.ColumnByteOffsets, srcOffsets.Length);
    }

    public readonly void RestoreTo(Archetype arch)
    {
        Debug.Assert(Count >= 0, "Backup entity count must be non-negative.");
        Debug.Assert(ColumnByteOffsets is not null, "Column offsets must not be null.");
        Debug.Assert(ColumnByteOffsets.Length == 0 || ColumnByteOffsets.Length == arch.ComponentTypes.Count,
            "Column offset count must match archetype component count (or be empty).");

        if (!IsChunked)
        {
            // Sanity: SourceCapacity should match what the archetype's
            // capacity was at capture time. If it doesn't (because
            // EnsureCapacity ran between capture and restore), that's
            // expected — RestoreFlatBackup handles offset translation.
            Debug.Assert(SourceCapacity > 0,
                "Non-chunked backup must have a positive SourceCapacity.");
            Debug.Assert((uint)Count <= (uint)arch.Capacity,
                $"Backup Count ({Count}) must fit in archetype capacity ({arch.Capacity}).");

            // Delegating to RestoreFlatBackup handles both:
            //
            // non-chunked → non-chunked: column-by-column copy with offset
            // translation from SourceColumnByteOffsets to current
            // _columnByteOffsets. Correct even when EnsureCapacity changed
            // the layout.
            //
            // non-chunked → chunked: Distributes flat backup across segments,
            // translating from backup-time offsets to segment-capacity offsets.
            arch.RestoreFlatBackup(Entities, Data, ColumnByteOffsets, Count);
        }
        else
        {
            Debug.Assert(SourceCapacity == 0,
                "Chunked backup must have SourceCapacity == 0 (sentinel).");
            Debug.Assert(SegmentCount <= arch.SegmentCount,
                "Backup segment count must not exceed the archetype's current " +
                "segment count (segments only grow between capture and restore).");

            arch.ResetCount();
            for (var i = 0; i < SegmentCount; i++)
            {
                ref var seg = ref arch.GetSegmentRef(i);
                var entityCount = SegmentCounts[i];

                Debug.Assert((uint)entityCount <= (uint)seg.Entities.Length,
                    "Backup segment entity count must fit in the destination segment.");
                Debug.Assert(entityCount >= 0,
                    "Backup per-segment entity count must be non-negative.");

                // Copy exactly entityCount entities and their component data.
                // Do NOT copy by seg.Entities.Length — that would copy stale
                // tail slots from pool-reused oversized arrays (BUG #2).
                // Do NOT copy by SegmentEntities[i].Length — that would
                // overflow the destination if pool reuse left a larger array
                // (BUG #2, original form).
                Array.Copy(SegmentEntities[i], seg.Entities, entityCount);
                seg.Count = entityCount;

                // Copy component data: for each column, copy entityCount rows
                // from the backup segment data to the destination segment data
                // at the current segment-capacity offsets.
                for (var col = 0; col < ColumnByteOffsets.Length; col++)
                {
                    var elemSize = arch.ComponentElementSize(col);
                    var columnBytes = entityCount * elemSize;
                    if (columnBytes <= 0) continue;

                    ref var srcRef = ref Unsafe.Add(
                        ref MemoryMarshal.GetArrayDataReference(SegmentData[i]),
                        ColumnByteOffsets[col]);
                    ref var dstRef = ref Unsafe.Add(
                        ref MemoryMarshal.GetArrayDataReference(seg.Data),
                        arch.ColumnByteOffsets[col]);
                    Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
                }
            }
            arch.SetCount(Count);
            arch.RebuildFlatEntities();
        }
    }
}
