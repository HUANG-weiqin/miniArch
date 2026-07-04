using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Zero-allocation enumerable over the live children of an entity.
/// Use <c>foreach</c> over the result of <see cref="World.EnumerateChildren"/>.
/// </summary>
public readonly struct ChildrenEnumerable
{
    private readonly Entity[] _childEntity;
    private readonly int[] _childNext;
    private readonly World _world;
    private readonly int _firstSlot;

    internal ChildrenEnumerable(Entity[] childEntity, int[] childNext, World world, int firstSlot)
    {
        _childEntity = childEntity;
        _childNext = childNext;
        _world = world;
        _firstSlot = firstSlot;
    }

    /// <summary>
    /// Returns an enumerator that iterates through the live children of the entity.
    /// </summary>
    public ChildrenEnumerator GetEnumerator() => new(_childEntity, _childNext, _world, _firstSlot);
}

/// <summary>
/// Enumerator over live children of an entity. Do not mutate the world
/// (AddChild/RemoveChild/Destroy) while an instance is in use.
/// </summary>
public struct ChildrenEnumerator
{
    private readonly Entity[] _childEntity;
    private readonly int[] _childNext;
    private readonly World _world;
    private int _slot;

    internal ChildrenEnumerator(Entity[] childEntity, int[] childNext, World world, int firstSlot)
    {
        _childEntity = childEntity;
        _childNext = childNext;
        _world = world;
        _slot = firstSlot;
        Current = default;
    }

    /// <summary>
    /// Gets the current child entity in the enumeration.
    /// </summary>
    public Entity Current { get; private set; }

    /// <summary>
    /// Advances the enumerator to the next live child entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (_slot >= 0)
        {
            var slot = _slot;
            _slot = _childNext[slot];
            var child = _childEntity[slot];
            if (!_world.IsAlive(child)) continue;

            Current = child;
            return true;
        }

        return false;
    }
}
