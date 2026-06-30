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
    /// Gets the first entity in the archetype that stores component
    /// <typeparamref name="T" />.
    /// Uses the same generic archetype cache as <see cref="Create{T}" />,
    /// so the hot path is O(1) with no allocation and no dictionary lookup.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No entity with component <typeparamref name="T" /> exists.
    /// </exception>
    public Entity GetFirst<T>() where T : unmanaged
    {
        ThrowIfDisposed();
        if (!TryGetCreateArchetype<T>(out var archetype) || archetype.EntityCount == 0)
        {
            throw new InvalidOperationException(
                $"No entity with component '{typeof(T).Name}' exists.");
        }
        return archetype.GetEntity(0);
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

    private QueryComponentSet CreateQueryComponentSet(ReadOnlySpan<Type> types)
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