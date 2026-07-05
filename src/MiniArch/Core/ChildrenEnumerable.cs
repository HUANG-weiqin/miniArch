using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

/// <summary>
/// Zero-allocation enumerable over the live children of an entity.
/// Use <c>foreach</c> over the result of <see cref="World.EnumerateChildren"/>.
/// </summary>
public readonly struct ChildrenEnumerable
{
    private readonly ChildSlot[] _childSlots;
    private readonly World _world;
    private readonly int _firstSlot;

    internal ChildrenEnumerable(ChildSlot[] childSlots, World world, int firstSlot)
    {
        _childSlots = childSlots;
        _world = world;
        _firstSlot = firstSlot;
    }

    /// <summary>
    /// Returns an enumerator that iterates through the live children of the entity.
    /// </summary>
    public ChildrenEnumerator GetEnumerator() => new(_childSlots, _world, _firstSlot);
}

/// <summary>
/// Enumerator over live children of an entity. Do not mutate the world
/// (AddChild/RemoveChild/Destroy) while an instance is in use.
/// </summary>
public struct ChildrenEnumerator
{
    private readonly ChildSlot[] _childSlots;
    private readonly World _world;
    private int _slot;

    internal ChildrenEnumerator(ChildSlot[] childSlots, World world, int firstSlot)
    {
        _childSlots = childSlots;
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
            _slot = _childSlots[slot].Next;
            var child = _childSlots[slot].Entity;
            if (!_world.IsAlive(child)) continue;

            Current = child;
            return true;
        }

        return false;
    }
}
