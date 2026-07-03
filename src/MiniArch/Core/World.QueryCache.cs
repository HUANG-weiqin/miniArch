using MiniArch.Core;

namespace MiniArch;

public sealed partial class World
{
    /// <summary>
    /// Gets an entity-only query from a description.
    /// </summary>
    public Query Query(in QueryDescription description)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
                CreateQueryComponentSet(description.GetAnyTypes()));
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
        while (true)
        {
            var snapshot = Volatile.Read(ref _archetypeSnapshot);
            var updated = new Archetype[snapshot.Length + 1];
            Array.Copy(snapshot, updated, snapshot.Length);
            updated[^1] = archetype;

            if (ReferenceEquals(Interlocked.CompareExchange(ref _archetypeSnapshot, updated, snapshot), snapshot))
            {
                return;
            }
        }
    }

}