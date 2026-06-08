using System.Collections;

namespace MiniArch.Core;

/// <summary>
/// Enumerable over matching chunks. Zero per-row overhead — iterate chunks then
/// use <see cref="Chunk.GetComponentSpan{T}(MiniArch.Core.ComponentType)"/> to access component data directly.
/// Slower than <c>EachSpan</c> for setup (resolves column index each call),
/// but faster per entity when many components are needed, and supports
/// unlimited component types with no wrapper allocation.
/// </summary>
internal readonly struct ChunkEnumerable : IEnumerable<Chunk>
{
    private readonly Query _query;

    internal ChunkEnumerable(Query query)
    {
        _query = query;
    }

    /// <summary>
    /// Returns a chunk enumerator.
    /// </summary>
    public ChunkEnumerator GetEnumerator() => new(_query);

    IEnumerator<Chunk> IEnumerable<Chunk>.GetEnumerator() => new ChunkEnumeratorAdapter(this);

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Chunk>)this).GetEnumerator();

    private sealed class ChunkEnumeratorAdapter : IEnumerator<Chunk>
    {
        private ChunkEnumerator _enumerator;

        public ChunkEnumeratorAdapter(ChunkEnumerable enumerable)
        {
            _enumerator = new ChunkEnumerator(enumerable._query);
        }

        public Chunk Current => _enumerator.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext() => _enumerator.MoveNext();

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Chunk enumerator.
/// </summary>
internal struct ChunkEnumerator
{
    private readonly Chunk[] _chunks;
    private readonly int _chunkCount;
    private int _chunkIndex;

    /// <summary>
    /// Creates an enumerator for a query.
    /// </summary>
    public ChunkEnumerator(Query query)
    {
        _chunks = query.GetChunkArray(out var chunkCount);
        _chunkCount = chunkCount;
        _chunkIndex = -1;
        Current = default!;
    }

    /// <summary>
    /// Gets the current chunk.
    /// </summary>
    public Chunk Current { get; private set; }

    /// <summary>
    /// Advances to the next chunk.
    /// </summary>
    public bool MoveNext()
    {
        _chunkIndex++;
        if (_chunkIndex < _chunkCount)
        {
            Current = _chunks[_chunkIndex];
            return true;
        }

        return false;
    }
}
