using System.Runtime.CompilerServices;

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
    private static unsafe void CopyComponentSlot(
        Chunk sourceChunk, int sourceRow,
        Chunk destChunk, int destRow,
        in CopyEntry entry)
    {
        var sourcePtr = sourceChunk.GetComponentBytePtr(entry.SourceColumnIndex, sourceRow);
        var destPtr = destChunk.GetComponentBytePtr(entry.DestinationColumnIndex, destRow);

        // Small copy specialization: use typed copy for common component sizes.
        // Avoids the overhead of Unsafe.CopyBlockUnaligned for typical structs.
        switch (entry.ByteSize)
        {
            case 4:
                *(int*)destPtr = *(int*)sourcePtr;
                break;
            case 8:
                *(long*)destPtr = *(long*)sourcePtr;
                break;
            case 12:
                *(long*)destPtr = *(long*)sourcePtr;
                *(int*)(destPtr + 8) = *(int*)(sourcePtr + 8);
                break;
            case 16:
                *(long*)destPtr = *(long*)sourcePtr;
                *(long*)(destPtr + 8) = *(long*)(sourcePtr + 8);
                break;
            default:
                Unsafe.CopyBlockUnaligned(destPtr, sourcePtr, (uint)entry.ByteSize);
                break;
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
