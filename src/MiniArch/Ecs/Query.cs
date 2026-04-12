namespace MiniArch.Ecs;

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
    private MiniArch.Core.Entity[]? _entities;
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
                    _current = Entity.FromCore(_entities[_rowIndex]);
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

/// <summary>
/// Single-component query facade.
/// </summary>
public readonly struct Query<T>
{
    private readonly MiniArch.Core.Query _query;
    private readonly MiniArch.Core.ComponentType _componentType;

    internal Query(MiniArch.Core.Query query, MiniArch.Core.ComponentType componentType)
    {
        _query = query;
        _componentType = componentType;
    }

    /// <summary>
    /// Gets the underlying advanced query.
    /// </summary>
    public MiniArch.Core.Query Advanced => _query;

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public QueryEnumerator<T> GetEnumerator() => new(_query, _componentType);
}

/// <summary>
/// Two-component query facade.
/// </summary>
public readonly struct Query<T1, T2>
{
    private readonly MiniArch.Core.Query _query;
    private readonly MiniArch.Core.ComponentType _componentType1;
    private readonly MiniArch.Core.ComponentType _componentType2;

    internal Query(
        MiniArch.Core.Query query,
        MiniArch.Core.ComponentType componentType1,
        MiniArch.Core.ComponentType componentType2)
    {
        _query = query;
        _componentType1 = componentType1;
        _componentType2 = componentType2;
    }

    /// <summary>
    /// Gets the underlying advanced query.
    /// </summary>
    public MiniArch.Core.Query Advanced => _query;

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public QueryEnumerator<T1, T2> GetEnumerator() => new(_query, _componentType1, _componentType2);
}

/// <summary>
/// Row view for a single-component query.
/// </summary>
public readonly struct QueryItem<T>
{
    private readonly T[]? _components;
    private readonly int _rowIndex;

    internal QueryItem(Entity entity, T[] components, int rowIndex)
    {
        Entity = entity;
        _components = components;
        _rowIndex = rowIndex;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Entity { get; }

    /// <summary>
    /// Gets the current component by reference.
    /// </summary>
    public ref readonly T Component
    {
        get
        {
            var components = _components ?? throw new InvalidOperationException("Query item is not initialized.");
            return ref components[_rowIndex];
        }
    }
}

/// <summary>
/// Row view for a two-component query.
/// </summary>
public readonly struct QueryItem<T1, T2>
{
    private readonly T1[]? _first;
    private readonly T2[]? _second;
    private readonly int _rowIndex;

    internal QueryItem(Entity entity, T1[] first, T2[] second, int rowIndex)
    {
        Entity = entity;
        _first = first;
        _second = second;
        _rowIndex = rowIndex;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Entity { get; }

    /// <summary>
    /// Gets the first component by reference.
    /// </summary>
    public ref readonly T1 First
    {
        get
        {
            var first = _first ?? throw new InvalidOperationException("Query item is not initialized.");
            return ref first[_rowIndex];
        }
    }

    /// <summary>
    /// Gets the second component by reference.
    /// </summary>
    public ref readonly T2 Second
    {
        get
        {
            var second = _second ?? throw new InvalidOperationException("Query item is not initialized.");
            return ref second[_rowIndex];
        }
    }
}

/// <summary>
/// Struct enumerator for single-component queries.
/// </summary>
public struct QueryEnumerator<T>
{
    private readonly MiniArch.Core.Chunk[] _chunks;
    private readonly MiniArch.Core.ComponentType _componentType;
    private int _chunkIndex;
    private int _rowIndex;
    private MiniArch.Core.Entity[]? _entities;
    private T[]? _components;
    private int _count;
    private QueryItem<T> _current;

    internal QueryEnumerator(MiniArch.Core.Query query, MiniArch.Core.ComponentType componentType)
    {
        _chunks = query.EnsureMatchingChunks();
        _componentType = componentType;
        _chunkIndex = -1;
        _rowIndex = -1;
        _entities = null;
        _components = null;
        _count = 0;
        _current = default;
    }

    /// <summary>
    /// Gets the current row view.
    /// </summary>
    public QueryItem<T> Current => _current;

    /// <summary>
    /// Advances to the next matching row.
    /// </summary>
    public bool MoveNext()
    {
        while (true)
        {
            if (_components is not null)
            {
                _rowIndex++;
                if (_rowIndex < _count)
                {
                    _current = new QueryItem<T>(Entity.FromCore(_entities![_rowIndex]), _components, _rowIndex);
                    return true;
                }
            }

            _chunkIndex++;
            if (_chunkIndex >= _chunks.Length)
            {
                return false;
            }

            var chunk = _chunks[_chunkIndex];
            if (!chunk.TryGetComponentIndex(_componentType, out var columnIndex) || chunk.Count == 0)
            {
                _components = null;
                continue;
            }

            _entities = chunk.GetEntityStorage();
            _components = chunk.GetTypedColumnStorageAt<T>(columnIndex);
            _count = chunk.Count;
            _rowIndex = -1;
        }
    }
}

/// <summary>
/// Struct enumerator for two-component queries.
/// </summary>
public struct QueryEnumerator<T1, T2>
{
    private readonly MiniArch.Core.Chunk[] _chunks;
    private readonly MiniArch.Core.ComponentType _componentType1;
    private readonly MiniArch.Core.ComponentType _componentType2;
    private int _chunkIndex;
    private int _rowIndex;
    private MiniArch.Core.Entity[]? _entities;
    private T1[]? _first;
    private T2[]? _second;
    private int _count;
    private QueryItem<T1, T2> _current;

    internal QueryEnumerator(
        MiniArch.Core.Query query,
        MiniArch.Core.ComponentType componentType1,
        MiniArch.Core.ComponentType componentType2)
    {
        _chunks = query.EnsureMatchingChunks();
        _componentType1 = componentType1;
        _componentType2 = componentType2;
        _chunkIndex = -1;
        _rowIndex = -1;
        _entities = null;
        _first = null;
        _second = null;
        _count = 0;
        _current = default;
    }

    /// <summary>
    /// Gets the current row view.
    /// </summary>
    public QueryItem<T1, T2> Current => _current;

    /// <summary>
    /// Advances to the next matching row.
    /// </summary>
    public bool MoveNext()
    {
        while (true)
        {
            if (_first is not null && _second is not null)
            {
                _rowIndex++;
                if (_rowIndex < _count)
                {
                    _current = new QueryItem<T1, T2>(Entity.FromCore(_entities![_rowIndex]), _first, _second, _rowIndex);
                    return true;
                }
            }

            _chunkIndex++;
            if (_chunkIndex >= _chunks.Length)
            {
                return false;
            }

            var chunk = _chunks[_chunkIndex];
            if (chunk.Count == 0
                || !chunk.TryGetComponentIndex(_componentType1, out var firstColumnIndex)
                || !chunk.TryGetComponentIndex(_componentType2, out var secondColumnIndex))
            {
                _first = null;
                _second = null;
                continue;
            }

            _entities = chunk.GetEntityStorage();
            _first = chunk.GetTypedColumnStorageAt<T1>(firstColumnIndex);
            _second = chunk.GetTypedColumnStorageAt<T2>(secondColumnIndex);
            _count = chunk.Count;
            _rowIndex = -1;
        }
    }
}
