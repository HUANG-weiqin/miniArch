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
    private readonly Entity _entity;

    internal EntityAccessor(Archetype archetype, int row, Entity entity)
    {
        _archetype = archetype;
        _row = row;
        _entity = entity;
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
    /// Throws if the entity does not have the component.
    /// For adding new components, use <c>CommandStream.Add&lt;T&gt;</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity does not have a component of type <typeparamref name="T"/>.</exception>
    /// <remarks>
    /// This method does NOT capture previous values for <see cref="ChangeQuery{T}.WithPreviousValues"/>
    /// — use <see cref="World.Set{T}"/> or CommandStream.Set when previous-value tracking is needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(in T value) where T : unmanaged
    {
        var columnIndex = _archetype.GetComponentIndexFast(Component<T>.ComponentType);
        if (columnIndex < 0)
            throw new InvalidOperationException(
                $"Entity {_entity} does not have component {typeof(T).Name}.");
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
