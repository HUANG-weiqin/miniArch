using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Public view of a chunk for batch component access.
/// In non-chunked mode, wraps the Archetype directly.
/// In chunked mode, represents a single segment within an Archetype.
/// </summary>
public readonly struct ChunkView
{
    private readonly Core.Archetype _archetype;
    private readonly int _segmentIndex; // -1 = non-chunked mode

    internal ChunkView(Core.Archetype archetype, int segmentIndex = -1)
    {
        _archetype = archetype;
        _segmentIndex = segmentIndex;
    }

/// <summary>Number of entities in this chunk.</summary>
public int Count => _segmentIndex >= 0
    ? _archetype.GetSegmentCount(_segmentIndex)
    : _archetype.EntityCount;

    /// <summary>Gets live entities as a span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities()
    {
        if (_archetype.IsChunked)
            return _archetype.GetSegmentEntities(_segmentIndex);
        return _archetype.GetEntities();
    }

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> for all rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetSpan<T>() where T : unmanaged
    {
        if (_archetype.IsChunked)
        {
            var colIdx = _archetype.GetComponentIndexFast(Component<T>.ComponentType);
            return _archetype.GetSegmentComponentSpan<T>(_segmentIndex, colIdx);
        }
        return _archetype.GetComponentSpan<T>(Component<T>.ComponentType);
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
        if (_archetype.IsChunked)
            return _archetype.GetSegmentComponentSpan<T>(_segmentIndex, columnIndex);
        return _archetype.GetComponentSpanAt<T>(columnIndex);
    }
}


