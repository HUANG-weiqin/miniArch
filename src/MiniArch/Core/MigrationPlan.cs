using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Pre-computed copy operations for archetype-to-archetype transitions.
/// Built once when an add/remove edge is first created, then reused on every migration.
/// </summary>
internal sealed class MigrationPlan
{
    /// <summary>Target archetype after migration.</summary>
    public readonly Archetype Destination;
    /// <summary>Copy entries for shared components (present in both source and destination).</summary>
    public readonly CopyEntry[] SharedCopies;
    /// <summary>For Add edges: column index in destination for the newly added component. Null for Remove edges.</summary>
    public readonly int? AddedComponentColumnIndex;

    public MigrationPlan(Archetype destination, CopyEntry[] sharedCopies, int? addedComponentColumnIndex = null)
    {
        Destination = destination;
        SharedCopies = sharedCopies;
        AddedComponentColumnIndex = addedComponentColumnIndex;
    }

    /// <summary>
    /// Builds a migration plan for the given source-to-destination archetype transition.
    /// </summary>
    /// <param name="source">Source archetype.</param>
    /// <param name="destination">Destination archetype.</param>
    /// <param name="changedComponent">The component being added or removed.</param>
    /// <param name="isAdd">True if adding a component, false if removing.</param>
    public static MigrationPlan Build(
        Archetype source,
        Archetype destination,
        ComponentType changedComponent,
        bool isAdd)
    {
        var sourceComponents = source.Signature.AsSpan();
        var destComponents = destination.Signature.AsSpan();
        var sharedCopies = new CopyEntry[sourceComponents.Length - (isAdd ? 0 : 1)];

        int? addedColumnIndex = null;
        var copyCount = 0;

        for (var di = 0; di < destComponents.Length; di++)
        {
            var destComponent = destComponents[di];

            // If this is the added component being added, record its column index and skip
            if (isAdd && destComponent == changedComponent)
            {
                addedColumnIndex = destination.GetComponentIndex(destComponent);
                continue;
            }

            // Find matching source column
            for (var si = 0; si < sourceComponents.Length; si++)
            {
                if (sourceComponents[si] == destComponent)
                {
                    var elementSize = destination.GetElementSize(di);
                    sharedCopies[copyCount++] = new CopyEntry(
                        source.GetComponentIndex(sourceComponents[si]),
                        destination.GetComponentIndex(destComponent),
                        elementSize);
                    break;
                }
            }
        }

        return new MigrationPlan(destination, sharedCopies, addedColumnIndex);
    }

    /// <summary>
    /// Executes shared component copies for a single entity migration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopySharedData(Chunk sourceChunk, int sourceRow, Chunk destChunk, int destRow)
    {
        var copies = SharedCopies;
        for (var i = 0; i < copies.Length; i++)
        {
            ref readonly var entry = ref copies[i];
            CopyComponentSlot(sourceChunk, sourceRow, destChunk, destRow, in entry);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyComponentSlot(
        Chunk sourceChunk, int sourceRow,
        Chunk destChunk, int destRow,
        in CopyEntry entry)
    {
        // Use ref-based access to avoid fixed/pin overhead of GetComponentBytePtr.
        ref var srcData = ref MemoryMarshal.GetArrayDataReference(sourceChunk.GetDataArray());
        ref var dstData = ref MemoryMarshal.GetArrayDataReference(destChunk.GetDataArray());

        var srcOffsets = sourceChunk.GetColumnByteOffsets();
        var dstOffsets = destChunk.GetColumnByteOffsets();
        var size = entry.ByteSize;

        ref var src = ref Unsafe.Add(ref srcData, srcOffsets[entry.SourceColumnIndex] + sourceRow * size);
        ref var dst = ref Unsafe.Add(ref dstData, dstOffsets[entry.DestinationColumnIndex] + destRow * size);

        CopySmall(ref dst, ref src, size);
    }

    /// <summary>
    /// Copies a small number of bytes using the fastest available method.
    /// Avoids Unsafe.CopyBlockUnaligned for tiny sizes; uses direct typed reads/writes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopySmall(ref byte dst, ref byte src, int size)
    {
        switch (size)
        {
            case 1:
                dst = src;
                return;
            case 4:
                Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<int>(ref src));
                return;
            case 8:
                Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<long>(ref src));
                return;
            case 12:
                Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<long>(ref src));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref src, 8)));
                return;
            case 16:
                Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<long>(ref src));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref src, 8)));
                return;
            default:
                Unsafe.CopyBlockUnaligned(ref dst, ref src, (uint)size);
                return;
        }
    }
}

/// <summary>
/// Describes a single component copy from one column to another during entity migration.
/// </summary>
internal readonly struct CopyEntry
{
    public readonly int SourceColumnIndex;
    public readonly int DestinationColumnIndex;
    public readonly int ByteSize;

    public CopyEntry(int sourceColumnIndex, int destinationColumnIndex, int byteSize)
    {
        SourceColumnIndex = sourceColumnIndex;
        DestinationColumnIndex = destinationColumnIndex;
        ByteSize = byteSize;
    }
}
