using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Points to a single entity row within the chunk array returned by
/// <c>world.Query(desc).GetChunks()</c>. Use <see cref="Component{T}"/>
/// to read a component value directly from the row.
/// Valid only while the world's archetype structure is unchanged.
/// </summary>
public readonly struct RowRef
{
    /// <summary>Index into the <c>GetChunks()</c> span.</summary>
    internal readonly int ChunkIndex;

    /// <summary>Row position within that chunk's entity/component spans.</summary>
    internal readonly int RowIndex;

    internal RowRef(int chunkIndex, int rowIndex)
    {
        ChunkIndex = chunkIndex;
        RowIndex = rowIndex;
    }

    /// <summary>Reads a component value directly from this row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Component<T>(ReadOnlySpan<ChunkView> chunks) where T : unmanaged
        => ref chunks[ChunkIndex].GetSpan<T>()[RowIndex];
}
