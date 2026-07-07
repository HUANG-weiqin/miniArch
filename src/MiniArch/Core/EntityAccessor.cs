using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Cached entity accessor for multiple component reads/writes on the same entity.
/// Performs the entity→(archetype,row) lookup once, then all subsequent
/// <see cref="Get{T}"/>, <see cref="Set{T}"/>, and <see cref="Has{T}"/> calls
/// operate directly on the cached location.
/// </summary>
/// <remarks>
/// This is a <c>ref struct</c> — stack-only, cannot be boxed, stored in fields,
/// captured in lambdas, or used with async. Discard it before any structural
/// change (Add/Remove) that may move the entity to a different archetype.
/// </remarks>
public ref struct EntityAccessor
{
    private readonly Archetype _archetype;
    private readonly int _row;

    internal EntityAccessor(Archetype archetype, int row)
    {
        _archetype = archetype;
        _row = row;
    }

    /// <summary>
    /// Gets a reference to the component <typeparamref name="T"/> on the accessed entity.
    /// Assumes the component exists; behaviour is undefined if it does not.
    /// Use <see cref="Has{T}"/> to check existence first if uncertain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>() where T : unmanaged
    {
        var columnIndex = _archetype.GetComponentIndexFast(Component<T>.ComponentType);
        return ref _archetype.GetComponentRefAt<T>(columnIndex, _row);
    }

    /// <summary>
    /// Writes a component value directly to the accessed entity.
    /// Assumes the component already exists on the entity (i.e. is part of the
    /// entity's archetype). For adding new components, use <c>CommandStream.Add&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    /// This method does NOT capture previous values for <see cref="ChangeQuery{T}.WithPreviousValues"/>
    /// — use <see cref="World.Set{T}"/> or CommandStream.Set when previous-value tracking is needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(in T value) where T : unmanaged
    {
        var columnIndex = _archetype.GetComponentIndexFast(Component<T>.ComponentType);
        _archetype.SetComponentAtTyped(columnIndex, _row, in value);
    }

    /// <summary>
    /// Returns whether the accessed entity has the component <typeparamref name="T"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() where T : unmanaged
    {
        return _archetype.TryGetComponentIndex(Component<T>.ComponentType, out _);
    }
}
