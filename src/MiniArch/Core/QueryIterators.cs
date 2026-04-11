using System.Collections;

namespace MiniArch.Core;

public readonly struct ChunkEnumerable : IEnumerable<Chunk>
{
    private readonly Query _query;

    internal ChunkEnumerable(Query query)
    {
        _query = query;
    }

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

public struct ChunkEnumerator
{
    private readonly Chunk[] _chunks;
    private int _chunkIndex;

    public ChunkEnumerator(Query query)
    {
        _chunks = query.EnsureMatchingChunks();
        _chunkIndex = -1;
        Current = default!;
    }

    public Chunk Current { get; private set; }

    public bool MoveNext()
    {
        _chunkIndex++;
        if (_chunkIndex < _chunks.Length)
        {
            Current = _chunks[_chunkIndex];
            return true;
        }

        return false;
    }
}
