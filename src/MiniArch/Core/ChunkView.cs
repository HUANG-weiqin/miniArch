using System.Diagnostics;
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Public view of a chunk for batch component access.
/// In non-chunked mode, wraps the Archetype directly.
/// In chunked mode, represents a single segment within an Archetype.
/// Supports slicing for intra-chunk parallelism in <see cref="Query.ForEachChunkParallel"/>.
/// </summary>
/// <remarks>
/// <b>Do not retain a <see cref="ChunkView"/> (or any span obtained from it)
/// across a structural change</b> (Add/Remove/Create/Destroy on any entity in
/// the same <see cref="World"/>). Structural changes may move entities between
/// archetypes or promote an archetype to chunked storage, invalidating the
/// internal row/segment mapping. Re-query each frame after all structural
/// changes are applied.
/// </remarks>
public readonly struct ChunkView
{
    private const int NonChunkedSegmentIndex = -1;

    private readonly Core.Archetype _archetype;
    private readonly int _segmentIndex; // NonChunkedSegmentIndex = non-chunked mode
    private readonly int _startRow;     // row offset, 0 for full views
    private readonly int _rowCount;     // -1 = use full count

    internal ChunkView(Core.Archetype archetype, int segmentIndex = NonChunkedSegmentIndex)
    {
        _archetype = archetype;
        _segmentIndex = segmentIndex;
        _startRow = 0;
        _rowCount = -1;
        AssertValid();
    }

    private ChunkView(Core.Archetype archetype, int segmentIndex, int startRow, int rowCount)
    {
        _archetype = archetype;
        _segmentIndex = segmentIndex;
        _startRow = startRow;
        _rowCount = rowCount;
        AssertValid();
    }

    [Conditional("DEBUG")]
    private void AssertValid()
    {
        Debug.Assert(_archetype is not null);
        if (_segmentIndex >= 0)
            Debug.Assert(_archetype.IsChunked, "Positive segment index requires chunked archetype.");
        else
            Debug.Assert(_segmentIndex == NonChunkedSegmentIndex);
        Debug.Assert(_rowCount >= -1);
        Debug.Assert(_startRow >= 0);
    }

    /// <summary>Gets the backing archetype.</summary>
    internal Core.Archetype Archetype => _archetype;

    /// <summary>Number of entities in this chunk (or slice).</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            AssertInitialized();
            return _rowCount >= 0
                ? _rowCount
                : _segmentIndex >= 0 ? _archetype.GetSegmentCount(_segmentIndex) : _archetype.EntityCount;
        }
    }

    /// <summary>Returns a sub-range view with the same backing storage.</summary>
    internal ChunkView Slice(int start, int length) =>
        new(_archetype, _segmentIndex, _startRow + start, length);

    [Conditional("DEBUG")]
    private void AssertInitialized()
    {
        Debug.Assert(_archetype is not null,
            "ChunkView was default-initialized; use Query.GetChunks() to obtain a valid view.");
    }

    /// <summary>Gets live entities as a span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        AssertInitialized();
        ReadOnlySpan<Entity> full;
        if (_archetype.IsChunked)
            full = _archetype.GetSegmentEntities(_segmentIndex);
        else
            full = _archetype.GetEntities();
        return _rowCount >= 0 ? full.Slice(_startRow, _rowCount) : full;
    }

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> for all rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetSpan<T>() where T : unmanaged
    {
        AssertInitialized();
        Debug.Assert(_archetype.TryGetComponentIndex(Component<T>.ComponentType, out _),
            $"ChunkView.GetSpan<{typeof(T).Name}>() called on archetype without this component. " +
            $"The chunk's archetype types: [{string.Join(", ", _archetype.ComponentTypes)}]. " +
            $"Use ChunkView.TryGetComponentIndex<T>() to check before calling GetSpan<T>() " +
            $"for optional components.");
        Span<T> full;
        if (_archetype.IsChunked)
        {
            var colIdx = _archetype.GetComponentIndexFast(Component<T>.ComponentType);
            full = _archetype.GetSegmentComponentSpan<T>(_segmentIndex, colIdx);
        }
        else
        {
            full = _archetype.GetFlatComponentSpan<T>(Component<T>.ComponentType);
        }
        return _rowCount >= 0 ? full.Slice(_startRow, _rowCount) : full;
    }

    /// <summary>
    /// Tries to get the column index for component type <typeparamref name="T"/>.
    /// Returns true if the chunk's archetype includes this component type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComponentIndex<T>(out int columnIndex) where T : unmanaged =>
        _archetype.TryGetComponentIndex(Component<T>.ComponentType, out columnIndex);

    /// <summary>
    /// Gets a component span at a pre-resolved column index.
    /// Use with <see cref="TryGetComponentIndex{T}"/> for efficient optional component access.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetComponentSpanAt<T>(int columnIndex) where T : unmanaged
    {
        AssertInitialized();
        Span<T> full;
        if (_archetype.IsChunked)
            full = _archetype.GetSegmentComponentSpan<T>(_segmentIndex, columnIndex);
        else
            full = _archetype.GetFlatComponentSpanAt<T>(columnIndex);
        return _rowCount >= 0 ? full.Slice(_startRow, _rowCount) : full;
    }
}
