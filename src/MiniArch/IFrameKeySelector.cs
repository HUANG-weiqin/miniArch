namespace MiniArch;

/// <summary>
/// Extracts a lookup key from an entity's row in chunk storage.
/// <c>Select</c> receives the entity handle, the full chunk span, and a
/// (chunkIndex, rowIndex) position. Read any component via
/// <c>chunks[chunkIndex].GetSpan&lt;T&gt;()[rowIndex]</c>.
/// </summary>
/// <remarks>
/// A build may call <see cref="Select"/> twice for each row. Starting from the
/// same selector value and world snapshot, both calls must return the same key.
/// Do not mutate the world or shared state from this method.
/// </remarks>
public interface IFrameKeySelector<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    TKey Select(Entity entity, ReadOnlySpan<ChunkView> chunks, int chunkIndex, int rowIndex);
}
