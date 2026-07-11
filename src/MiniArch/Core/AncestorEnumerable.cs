using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Zero-allocation enumerable over the ancestors of an entity.
/// Use <c>foreach</c> over the result of <see cref="World.EnumerateAncestors"/>.
/// Yields parent, grandparent, great-grandparent, … up to the root.
/// </summary>
public readonly struct AncestorEnumerable
{
    private readonly Entity[] _parentByChild;
    private readonly World _world;
    private readonly Entity _child;
    private readonly int _entitySlotCount;

    internal AncestorEnumerable(Entity[] parentByChild, World world, Entity child, int entitySlotCount)
    {
        _parentByChild = parentByChild;
        _world = world;
        _child = child;
        _entitySlotCount = entitySlotCount;
    }

    /// <summary>
    /// Returns an enumerator that iterates through the ancestor chain.
    /// </summary>
    public AncestorEnumerator GetEnumerator() => new(_parentByChild, _world, _child, _entitySlotCount);
}

/// <summary>
/// Enumerator over ancestors of an entity. Do not mutate the world
/// (AddChild/RemoveChild/Destroy) while an instance is in use.
/// </summary>
public struct AncestorEnumerator
{
    private readonly Entity[] _parentByChild;
    private readonly World _world;
    private readonly int _maxSteps;
    private Entity _current;
    private int _step;

    internal AncestorEnumerator(Entity[] parentByChild, World world, Entity child, int entitySlotCount)
    {
        _parentByChild = parentByChild;
        _world = world;
        _current = child;
        _maxSteps = entitySlotCount; // a valid chain can span at most all entities
        _step = 0;
    }

    /// <summary>
    /// Gets the current ancestor entity in the enumeration.
    /// </summary>
    public Entity Current { get; private set; }

    /// <summary>
    /// Advances the enumerator to the next ancestor.
    /// Only yields live ancestors (version-correct parent entities).
    /// Stops when the parent is dead or does not exist.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (_current.IsValid)
        {
            // Verify _current is still alive before trusting its parent lookup.
            // A stale handle (slot reused by a different entity) would read the
            // wrong parent from _parentByChild and traverse into unrelated ancestry.
            if (!_world.IsAlive(_current))
                break;

            if (_step >= _maxSteps)
                throw new InvalidOperationException(
                    $"Hierarchy cycle detected or chain too long (≥{_maxSteps}). " +
                    "The entity parent chain exceeds the maximum possible length. " +
                    "Use WorldValidator to check for structural corruption.");

            var id = _current.Id;
            Entity parent;
            if ((uint)id < (uint)_parentByChild.Length)
                parent = _parentByChild[id];
            else
                parent = default;

            // No parent → stop (root).
            if (!parent.IsValid)
                break;

            // Dead or stale (version mismatch) → stop.
            // A destroyed parent whose slot was reused will fail this check,
            // preventing traversal into an unrelated entity's parent chain.
            if (!_world.IsAlive(parent))
                break;

            Current = parent;
            _current = parent;
            _step++;
            return true;
        }

        Current = default;
        return false;
    }
}
