using System.Buffers;

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

    /// <summary>
    /// Returns an ordered entity enumerable using an internally pooled buffer per enumeration.
    /// </summary>
    public OrderedQuery OrderBy(IComparer<Entity> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        return new OrderedQuery(_query, comparer);
    }

    /// <summary>
    /// Returns an ordered entity enumerable using an internally pooled buffer per enumeration.
    /// </summary>
    public OrderedQuery OrderBy(Comparison<Entity> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return OrderBy(Comparer<Entity>.Create(comparison));
    }
}

/// <summary>
/// Struct enumerator over entities.
/// </summary>
public struct QueryEnumerator
{
    private readonly MiniArch.Core.Chunk[] _chunks;
    private readonly int _chunkCount;
    private int _chunkIndex;
    private int _rowIndex;
    private Entity[]? _entities;
    private int _count;
    private Entity _current;

    internal QueryEnumerator(MiniArch.Core.Query query)
    {
        _chunks = query.GetChunkArray(out var chunkCount);
        _chunkCount = chunkCount;
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
            if (_chunkIndex >= _chunkCount)
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

/// <summary>
/// Entity-only ordered query facade that materializes and sorts results per enumeration.
/// </summary>
public readonly struct OrderedQuery
{
    private readonly MiniArch.Core.Query _query;
    private readonly IComparer<Entity> _comparer;

    internal OrderedQuery(MiniArch.Core.Query query, IComparer<Entity> comparer)
    {
        _query = query;
        _comparer = comparer;
    }

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public OrderedQueryEnumerator GetEnumerator() => new(_query, _comparer);
}

/// <summary>
/// Struct enumerator over sorted entities.
/// </summary>
public struct OrderedQueryEnumerator : IDisposable
{
    private readonly MiniArch.Core.Query _query;
    private readonly IComparer<Entity> _comparer;
    private Entity[]? _entities;
    private int _count;
    private int _index;
    private bool _initialized;
    private Entity _current;

    internal OrderedQueryEnumerator(MiniArch.Core.Query query, IComparer<Entity> comparer)
    {
        _query = query;
        _comparer = comparer;
        _entities = null;
        _count = 0;
        _index = -1;
        _initialized = false;
        _current = default;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Current => _current;

    /// <summary>
    /// Advances to the next sorted entity.
    /// </summary>
    public bool MoveNext()
    {
        if (!_initialized)
        {
            Initialize();
        }

        _index++;
        if (_index >= _count)
        {
            return false;
        }

        _current = _entities![_index];
        return true;
    }

    /// <summary>
    /// Returns the rented sort buffer.
    /// </summary>
    public void Dispose()
    {
        var entities = _entities;
        if (entities is null)
        {
            return;
        }

        _entities = null;
        _count = 0;
        ArrayPool<Entity>.Shared.Return(entities);
    }

    private void Initialize()
    {
        _initialized = true;

        var chunks = _query.GetChunkArray(out var chunkCount);
        var count = 0;
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            count += chunks[chunkIndex].Count;
        }

        if (count == 0)
        {
            return;
        }

        var entities = ArrayPool<Entity>.Shared.Rent(count);
        _entities = entities;
        _count = count;

        var entityIndex = 0;
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var storage = chunk.GetEntityStorage();
            var rowCount = chunk.Count;
            Array.Copy(storage, 0, entities, entityIndex, rowCount);
            entityIndex += rowCount;
        }

        Array.Sort(entities, 0, count, _comparer);
    }
}
