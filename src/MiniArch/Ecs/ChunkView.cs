using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Public view of a chunk for batch component access.
/// Wraps the internal Archetype directly to maximize JIT inlining.
/// </summary>
public readonly struct ChunkView
{
    private readonly Core.Archetype _archetype;

    internal ChunkView(Core.Archetype archetype) => _archetype = archetype;

    /// <summary>Number of entities in this chunk.</summary>
    public int Count => _archetype.EntityCount;

    /// <summary>Gets live entities as a span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Entity> GetEntities() => _archetype.GetEntities();

    /// <summary>
    /// Gets a span of component <typeparamref name="T"/> for all rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetSpan<T>() where T : unmanaged =>
        _archetype.GetComponentSpan<T>(Component<T>.ComponentType);

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
    public Span<T> GetComponentSpanAt<T>(int columnIndex) where T : unmanaged =>
        _archetype.GetComponentSpanAt<T>(columnIndex);
}


