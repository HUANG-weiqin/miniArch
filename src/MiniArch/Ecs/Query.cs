namespace MiniArch;

/// <summary>
/// Entity-only query facade that supports direct foreach enumeration.
/// </summary>
public readonly struct Query
{
    private readonly MiniArch.Core.Query _query;

    internal Query(MiniArch.Core.Query query)
    {
        _query = query;
    }

    /// <summary>
    /// Gets the underlying advanced query.
    /// </summary>
    public MiniArch.Core.Query Advanced => _query;

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public QueryEnumerator GetEnumerator() => new(_query);
}

/// <summary>
/// Struct enumerator over entities.
/// </summary>
public struct QueryEnumerator
{
    private readonly MiniArch.Core.Chunk[] _chunks;
    private int _chunkIndex;
    private int _rowIndex;
    private Entity[]? _entities;
    private int _count;
    private Entity _current;

    internal QueryEnumerator(MiniArch.Core.Query query)
    {
        _chunks = query.EnsureMatchingChunks();
        _chunkIndex = -1;
        _rowIndex = -1;
        _entities = null;
        _count = 0;
        _current = default;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Current => _current;

    /// <summary>
    /// Advances to the next entity.
    /// </summary>
    public bool MoveNext()
    {
        while (true)
        {
            if (_entities is not null)
            {
                _rowIndex++;
                if (_rowIndex < _count)
                {
                    _current = _entities[_rowIndex];
                    return true;
                }
            }

            _chunkIndex++;
            if (_chunkIndex >= _chunks.Length)
            {
                return false;
            }

            var chunk = _chunks[_chunkIndex];
            if (chunk.Count == 0)
            {
                _entities = null;
                continue;
            }

            _entities = chunk.GetEntityStorage();
            _count = chunk.Count;
            _rowIndex = -1;
        }
    }
}
