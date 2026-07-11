namespace MiniArch;

/// <summary>
/// Extracts a lookup key from an entity's row in chunk storage.
/// <c>Select</c> receives the entity handle, the full chunk span, and a
/// (chunkIndex, rowIndex) position. Read any component via
/// <c>chunks[chunkIndex].GetSpan&lt;T&gt;()[rowIndex]</c>.
/// </summary>
public interface IFrameKeySelector<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    TKey Select(Entity entity, ReadOnlySpan<ChunkView> chunks, int chunkIndex, int rowIndex);
}
