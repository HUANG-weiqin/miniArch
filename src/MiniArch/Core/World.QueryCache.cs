using System.Diagnostics;
using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Gets an entity-only query from a description.
    /// </summary>
    public Query Query(in QueryDescription description)
    {
        AssertNotDisposed();
        return new Query(GetAdvancedQuery(in description));
    }

    /// <summary>
    /// Returns the single entity that has component <typeparamref name="T"/>.
    /// Intended for global/state components that exist on exactly one entity
    /// (e.g. game settings, turn state, camera). Scans every archetype, so
    /// this is an O(archetypes) cold path — for multi-entity access use
    /// <see cref="Query(in QueryDescription)" />.
    /// </summary>
    /// <remarks>
    /// Unlike the old <c>GetFirst&lt;T&gt;</c>, this finds the entity
    /// regardless of which archetype stores it (single- or multi-component).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when zero entities have component <typeparamref name="T" />,
    /// or when more than one entity has it (the singleton contract is violated).
    /// </exception>
    public Entity GetSingleton<T>() where T : unmanaged
    {
        // NOTE: O(archetype_count) scan. Even with 100K archetypes this is < 1ms.
        // Marked as cold path — callers should cache the result. Not worth caching
        // internally (adds invalidation complexity on every archetype change).
        // See .knowledge/kb-hardening-roadmap.md §M2.4.
        AssertNotDisposed();
        var componentType = Component<T>.ComponentType;
        Entity found = default;
        var seen = 0;
        foreach (var archetype in _archetypes.Values)
        {
            if (!archetype.TryGetComponentIndex(componentType, out _)) continue;
            var count = archetype.EntityCount;
            if (count == 0) continue;
            if (seen == 0)
                found = archetype.GetEntity(0);
            seen += count;
        }

        if (seen == 0)
            throw new InvalidOperationException(
                $"No entity has component '{typeof(T).Name}'.");
        if (seen > 1)
            throw new InvalidOperationException(
                $"GetSingleton<{typeof(T).Name}>(): expected exactly one entity with this component, but found {seen}.");

        return found;
    }

    internal MiniArch.Core.QueryCache GetAdvancedQuery(in QueryDescription description)
    {
        return GetOrCreateQuery(GetOrCreateQueryFilter(description));
    }

    internal MiniArch.Core.QueryCache GetOrCreateQuery(QueryFilter filter)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queries);
            if (snapshot.TryGetValue(filter, out var query))
            {
                return query;
            }

            var candidate = new MiniArch.Core.QueryCache(this, filter);
            var updated = new Dictionary<QueryFilter, MiniArch.Core.QueryCache>(snapshot)
            {
                [filter] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queries, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private QueryFilter GetOrCreateQueryFilter(in QueryDescription description)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _queryFiltersByDescription);
            if (snapshot.TryGetValue(description, out var filter))
            {
                return filter;
            }

            var candidate = new QueryFilter(
                CreateQueryComponentSet(description.GetRequiredTypes()),
                CreateQueryComponentSet(description.GetExcludedTypes()),
                CreateQueryComponentSet(description.GetAnyTypes()),
                exact: description.IsExact);
            var updated = new Dictionary<QueryDescription, QueryFilter>(snapshot)
            {
                [description] = candidate
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _queryFiltersByDescription, updated, snapshot), snapshot))
            {
                return candidate;
            }
        }
    }

    private static QueryComponentSet CreateQueryComponentSet(ReadOnlySpan<Type> types)
    {
        if (types.Length == 0)
        {
            return QueryComponentSet.Empty;
        }

        var componentTypes = new ComponentType[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            componentTypes[i] = ComponentRegistry.Shared.GetOrCreate(types[i]);
        }

        return QueryComponentSet.CreateFrom(componentTypes);
    }

    private void PublishArchetypeSnapshot(Archetype archetype)
    {
        // Sorted insertion by signature so that query iteration order is a
        // deterministic function of component types, not of creation history.
        // This eliminates order sensitivity to Clone (which may skip empty
        // archetypes), RestoreState (which leaves mutation-created artifacts),
        // and any other path that produces a different creation order.
        while (true)
        {
            var snapshot = Volatile.Read(ref _archetypeSnapshot);
            var idx = FindInsertIndex(archetype.Signature, snapshot.AsSpan(0, snapshot.Length));
            var updated = new Archetype[snapshot.Length + 1];
            Array.Copy(snapshot, 0, updated, 0, idx);
            Array.Copy(snapshot, idx, updated, idx + 1, snapshot.Length - idx);
            updated[idx] = archetype;
            AssertSnapshotSorted(updated);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _archetypeSnapshot, updated, snapshot), snapshot))
            {
                return;
            }
        }
    }

    [Conditional("DEBUG")]
    private void AssertSnapshotSorted(Archetype[] snapshot)
    {
        for (var i = 1; i < snapshot.Length; i++)
            Debug.Assert(CompareSignatures(snapshot[i - 1].Signature, snapshot[i].Signature) <= 0,
                $"Archetype snapshot is not sorted at index {i}.");
    }

    /// <summary>
    /// Binary search for the sorted insertion point of a signature in the
    /// archetype snapshot. Returns the index at which to insert.
    /// </summary>
    private static int FindInsertIndex(Signature signature, ReadOnlySpan<Archetype> snapshot)
    {
        int lo = 0, hi = snapshot.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (CompareSignatures(signature, snapshot[mid].Signature) < 0)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// Lexicographic comparison of two Signatures by ComponentType.Value.
    /// Shorter signature sorts before longer when all elements are equal.
    /// </summary>
    private static int CompareSignatures(Signature a, Signature b)
    {
        var sa = a.AsSpan();
        var sb = b.AsSpan();
        var n = Math.Min(sa.Length, sb.Length);
        for (var i = 0; i < n; i++)
        {
            var cmp = sa[i].Value.CompareTo(sb[i].Value);
            if (cmp != 0) return cmp;
        }
        return sa.Length.CompareTo(sb.Length);
    }
}