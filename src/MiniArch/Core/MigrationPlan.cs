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

        // All chunks in an archetype share the same column byte offsets (same component layout).
        var sourceOffsets = source.GetChunk(0).GetColumnByteOffsets();
        var destOffsets = destination.GetChunk(0).GetColumnByteOffsets();

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

                var sourceCol = source.GetComponentIndex(sourceComponents[sourceIndex]);
                var destCol = destination.GetComponentIndex(destinationComponent);
                sharedCopies[copyCount++] = new CopyEntry(
                    sourceOffsets[sourceCol],
                    destOffsets[destCol],
                    destination.GetElementSize(destinationIndex));
                break;
            }
        }

        return new MigrationPlan(destination, sharedCopies, addedColumnIndex);
    }

    /// <summary>
    /// Copies all shared component data from the source row to the destination row.
    /// Column byte offsets are pre-computed in <see cref="CopyEntry"/>;
    /// chunk data array references are hoisted once outside the loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopySharedData(Chunk sourceChunk, int sourceRow, Chunk destinationChunk, int destinationRow)
    {
        ref var sourceBase = ref MemoryMarshal.GetArrayDataReference(sourceChunk.GetDataArray());
        ref var destBase = ref MemoryMarshal.GetArrayDataReference(destinationChunk.GetDataArray());
        var copies = SharedCopies;

        for (var i = 0; i < copies.Length; i++)
        {
            ref readonly var entry = ref copies[i];
            var size = entry.ByteSize;
            Chunk.CopySmall(
                ref Unsafe.Add(ref destBase, entry.DestColumnByteOffset + destinationRow * size),
                ref Unsafe.Add(ref sourceBase, entry.SourceColumnByteOffset + sourceRow * size),
                size);
        }
    }
}

internal readonly struct CopyEntry
{
    /// <summary>Pre-computed byte offset of the source column within the chunk data buffer.</summary>
    internal readonly int SourceColumnByteOffset;
    /// <summary>Pre-computed byte offset of the destination column within the chunk data buffer.</summary>
    internal readonly int DestColumnByteOffset;
    internal readonly int ByteSize;

    internal CopyEntry(int sourceColumnByteOffset, int destColumnByteOffset, int byteSize)
    {
        SourceColumnByteOffset = sourceColumnByteOffset;
        DestColumnByteOffset = destColumnByteOffset;
        ByteSize = byteSize;
    }
}
