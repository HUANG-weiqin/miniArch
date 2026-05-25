namespace Hero.Ecs;

/// <summary>
/// A zero-GC, read-only view of trigger match targets produced by <see cref="TriggerSystem"/>.
/// Supports <c>foreach</c> enumeration without heap allocation.
/// </summary>
public readonly struct TriggerTargets
{
    private readonly MiniArch.Entity[] _items;
    private readonly int _count;

    /// <summary>Number of matched targets.</summary>
    public int Count => _count;

    /// <summary>Zero-GC enumerator for <c>foreach</c>.</summary>
    public Enumerator GetEnumerator() => new(_items, _count);

    internal TriggerTargets(MiniArch.Entity[] items, int count)
    {
        _items = items;
        _count = count;
    }

    /// <summary>
    /// Value-type enumerator — avoids boxing and heap allocation.
    /// </summary>
    public struct Enumerator
    {
        private readonly MiniArch.Entity[] _items;
        private readonly int _count;
        private int _index;

        internal Enumerator(MiniArch.Entity[] items, int count)
        {
            _items = items;
            _count = count;
            _index = -1;
        }

        public MiniArch.Entity Current => _items[_index];
        public bool MoveNext() => ++_index < _count;
    }
}
