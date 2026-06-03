using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Pre-computed copy operations for one archetype-to-archetype transition.
/// </summary>
internal sealed class MigrationPlan
{
    internal readonly Archetype Destination;
    internal readonly CopyEntry[] SharedCopies;
    internal readonly int? AddedComponentColumnIndex;

    private MigrationPlan(Archetype destination, CopyEntry[] sharedCopies, int? addedComponentColumnIndex)
    {
        Destination = destination;
        SharedCopies = sharedCopies;
        AddedComponentColumnIndex = addedComponentColumnIndex;
    }

    internal static MigrationPlan Build(
        Archetype source,
        Archetype destination,
        ComponentType changedComponent,
        bool isAdd)
    {
        var sourceComponents = source.Signature.AsSpan();
        var destinationComponents = destination.Signature.AsSpan();
        var sharedCopies = new CopyEntry[sourceComponents.Length - (isAdd ? 0 : 1)];

        int? addedColumnIndex = null;
        var copyCount = 0;

        for (var destinationIndex = 0; destinationIndex < destinationComponents.Length; destinationIndex++)
        {
            var destinationComponent = destinationComponents[destinationIndex];
            if (isAdd && destinationComponent == changedComponent)
            {
                addedColumnIndex = destination.GetComponentIndex(destinationComponent);
                continue;
            }

            for (var sourceIndex = 0; sourceIndex < sourceComponents.Length; sourceIndex++)
            {
                if (sourceComponents[sourceIndex] != destinationComponent)
                {
                    continue;
                }

                sharedCopies[copyCount++] = new CopyEntry(
                    source.GetComponentIndex(sourceComponents[sourceIndex]),
                    destination.GetComponentIndex(destinationComponent),
                    destination.GetElementSize(destinationIndex));
                break;
            }
        }

        return new MigrationPlan(destination, sharedCopies, addedColumnIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopySharedData(Chunk sourceChunk, int sourceRow, Chunk destinationChunk, int destinationRow)
    {
        var copies = SharedCopies;
        for (var i = 0; i < copies.Length; i++)
        {
            ref readonly var entry = ref copies[i];
            CopyComponentSlot(sourceChunk, sourceRow, destinationChunk, destinationRow, in entry);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyComponentSlot(
        Chunk sourceChunk,
        int sourceRow,
        Chunk destinationChunk,
        int destinationRow,
        in CopyEntry entry)
    {
        ref var sourceData = ref MemoryMarshal.GetArrayDataReference(sourceChunk.GetDataArray());
        ref var destinationData = ref MemoryMarshal.GetArrayDataReference(destinationChunk.GetDataArray());
        var sourceOffsets = sourceChunk.GetColumnByteOffsets();
        var destinationOffsets = destinationChunk.GetColumnByteOffsets();
        var size = entry.ByteSize;

        ref var source = ref Unsafe.Add(ref sourceData, sourceOffsets[entry.SourceColumnIndex] + sourceRow * size);
        ref var destination = ref Unsafe.Add(ref destinationData, destinationOffsets[entry.DestinationColumnIndex] + destinationRow * size);
        Chunk.CopySmall(ref destination, ref source, size);
    }
}

internal readonly struct CopyEntry
{
    internal readonly int SourceColumnIndex;
    internal readonly int DestinationColumnIndex;
    internal readonly int ByteSize;

    internal CopyEntry(int sourceColumnIndex, int destinationColumnIndex, int byteSize)
    {
        SourceColumnIndex = sourceColumnIndex;
        DestinationColumnIndex = destinationColumnIndex;
        ByteSize = byteSize;
    }
}
