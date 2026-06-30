using System.Collections;

namespace MiniArch.Core;

/// <summary>
/// Enumerable over matching archetypes. Zero per-row overhead — iterate archetypes then
/// use <see cref="Archetype.GetComponentSpan{T}(MiniArch.Core.ComponentType)"/> to access component data directly.
/// Slower than <c>EachSpan</c> for setup (resolves column index each call),
/// but faster per entity when many components are needed, and supports
/// unlimited component types with no wrapper allocation.
/// </summary>
internal readonly struct ArchetypeEnumerable : IEnumerable<Archetype>
{
    private readonly QueryCache _query;

    internal ArchetypeEnumerable(QueryCache query)
    {
        _query = query;
    }

    /// <summary>
    /// Returns an archetype enumerator.
    /// </summary>
    public ArchetypeEnumerator GetEnumerator() => new(_query);

    IEnumerator<Archetype> IEnumerable<Archetype>.GetEnumerator() => new ArchetypeEnumeratorAdapter(this);

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Archetype>)this).GetEnumerator();

    private sealed class ArchetypeEnumeratorAdapter : IEnumerator<Archetype>
    {
        private ArchetypeEnumerator _enumerator;

        public ArchetypeEnumeratorAdapter(ArchetypeEnumerable enumerable)
        {
            _enumerator = new ArchetypeEnumerator(enumerable._query);
        }

        public Archetype Current => _enumerator.Current;

        object IEnumerator.Current => Current;

        public bool MoveNext() => _enumerator.MoveNext();

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Archetype enumerator.
/// </summary>
internal struct ArchetypeEnumerator
{
    private readonly Archetype[] _archetypes;
    private readonly int _archetypeCount;
    private int _archetypeIndex;

    /// <summary>
    /// Creates an enumerator for a query.
    /// </summary>
    public ArchetypeEnumerator(QueryCache query)
    {
        _archetypes = query.GetArchetypeArray(out var archetypeCount);
        _archetypeCount = archetypeCount;
        _archetypeIndex = -1;
        Current = default!;
    }

    /// <summary>
    /// Gets the current archetype.
    /// </summary>
    public Archetype Current { get; private set; }

    /// <summary>
    /// Advances to the next archetype.
    /// </summary>
    public bool MoveNext()
    {
        _archetypeIndex++;
        if (_archetypeIndex < _archetypeCount)
        {
            Current = _archetypes[_archetypeIndex];
            return true;
        }

        return false;
    }
}
