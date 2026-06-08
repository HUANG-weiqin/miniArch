using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Public view of a chunk for batch component access.
/// Wraps the internal Chunk type so users never depend on Core.Chunk directly.
/// </summary>
public readonly struct ChunkView
{
    private readonly Core.Chunk _chunk;

    internal ChunkView(Core.Chunk chunk) => _chunk = chunk;

    /// <summary>Number of entities in this chunk.</summary>
    public int Count => _chunk.Count;

    /// <summary>Gets live entities as a span.</summary>
    public ReadOnlySpan<Entity> GetEntities() => _chunk.GetEntities();

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> for all rows.
    /// </summary>
    public Span<T> GetSpan<T>() where T : struct => _chunk.GetComponentSpan<T>();
}

/// <summary>
/// Enumerable over matching chunks. Use with <c>foreach</c>.
/// </summary>
public readonly struct ChunkViewEnumerable
{
    private readonly Core.Query _query;

    internal ChunkViewEnumerable(Core.Query query) => _query = query;

    /// <summary>Returns a chunk enumerator.</summary>
    public ChunkViewEnumerator GetEnumerator() => new(_query);
}

/// <summary>
/// Enumerator over matching chunks.
/// </summary>
public ref struct ChunkViewEnumerator
{
    private readonly Core.Chunk[] _chunks;
    private readonly int _count;
    private int _index;

    internal ChunkViewEnumerator(Core.Query query)
    {
        _chunks = query.GetChunkArray(out _count);
        _index = -1;
        Current = default;
    }

    /// <summary>Gets the current chunk view.</summary>
    public ChunkView Current { get; private set; }

    /// <summary>Advances to the next chunk.</summary>
    public bool MoveNext()
    {
        _index++;
        if (_index < _count)
        {
            Current = new ChunkView(_chunks[_index]);
            return true;
        }
        return false;
    }
}
