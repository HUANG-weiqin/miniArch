namespace MiniArch;

/// <summary>
/// Deterministic query that partitions entities by the value of
/// <typeparamref name="TComponent"/>. No manual Add/Set/Remove sidecar calls needed:
/// every public read method rebuilds from the ECS <see cref="World"/> on demand.
/// </summary>
/// <typeparam name="TComponent">Component type used as bucket key. Must define value equality.</typeparam>
/// <remarks>
/// <para>
/// <b>Internal structure</b>: This type has no internal buffer. Each call to <see cref="Get"/>
/// or <see cref="TryGet"/> scans the world for the requested key and writes matching entities
/// into the caller-provided span. The caller is responsible for providing a span large enough
/// to hold all results.
/// </para>
/// <para>
/// <b>Freshness</b>: every public read method (<see cref="Get"/>, <see cref="TryGet"/>,
/// <see cref="ContainsKey"/>, <see cref="Count"/>) performs a deterministic scan from the
/// real World for the <b>requested key only</b>. Correctness is guaranteed — there are no
/// probabilistic fast paths, fingerprints, or adaptive dirty-mode heuristics.
/// There is no full-cache refresh API; each call re-scans only the requested key.
/// </para>
/// <para>
/// <b>Thread safety</b>: not thread-safe. Intended for single-threaded game-loop use.
/// </para>
/// </remarks>
public sealed class ComponentBucketQuery<TComponent>
    where TComponent : unmanaged, IEquatable<TComponent>
{
    private readonly World _world;
    private readonly QueryDescription _scope;

    /// <summary>
    /// Creates a query over all entities that have <typeparamref name="TComponent"/>.
    /// </summary>
    public ComponentBucketQuery(World world)
        : this(world, new QueryDescription().With<TComponent>())
    {
    }

    /// <summary>
    /// Creates a query over entities matching <paramref name="scope"/>.
    /// If <paramref name="scope"/> does not include <c>With&lt;TComponent&gt;()</c>,
    /// it is automatically added so the query can read the key component.
    /// </summary>
    public ComponentBucketQuery(World world, QueryDescription scope)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;

        // Ensure the scope includes TComponent so we can read the key value.
        var hasRequired = false;
        var requiredTypes = scope.GetRequiredTypes();
        for (var i = 0; i < requiredTypes.Length; i++)
        {
            if (requiredTypes[i] == typeof(TComponent))
            {
                hasRequired = true;
                break;
            }
        }

        if (!hasRequired)
            scope = scope.With<TComponent>();

        _scope = scope;
    }

    /// <summary>
    /// Returns the number of entities whose <typeparamref name="TComponent"/> equals <paramref name="key"/>.
    /// </summary>
    public int Count(TComponent key)
    {
        var query = _world.Query(in _scope);
        var chunks = query.GetChunks();

        var total = 0;
        foreach (var chunk in chunks)
        {
            var components = chunk.GetSpan<TComponent>();
            var count = chunk.Count;

            for (var i = 0; i < count; i++)
            {
                if (components[i].Equals(key))
                    total++;
            }
        }

        return total;
    }

    /// <summary>
    /// Whether any entity currently has <typeparamref name="TComponent"/> equal to <paramref name="key"/>.
    /// </summary>
    public bool ContainsKey(TComponent key)
    {
        var query = _world.Query(in _scope);
        var chunks = query.GetChunks();

        foreach (var chunk in chunks)
        {
            var components = chunk.GetSpan<TComponent>();
            var count = chunk.Count;

            for (var i = 0; i < count; i++)
            {
                if (components[i].Equals(key))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes all entities whose <typeparamref name="TComponent"/> value equals <paramref name="key"/>
    /// into <paramref name="destination"/> and returns the number of entities written.
    /// If <paramref name="destination"/> is too small, only the first N matching entities are written.
    /// </summary>
    /// <returns>The number of matching entities written to <paramref name="destination"/>.</returns>
    public int Get(TComponent key, Span<Entity> destination)
    {
        var written = 0;
        var query = _world.Query(in _scope);
        var chunks = query.GetChunks();

        foreach (var chunk in chunks)
        {
            var entities = chunk.GetEntities();
            var components = chunk.GetSpan<TComponent>();
            var count = chunk.Count;

            for (var i = 0; i < count; i++)
            {
                if (!components[i].Equals(key))
                    continue;

                if (written < destination.Length)
                    destination[written] = entities[i];
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Tries to get entities whose <typeparamref name="TComponent"/> value equals <paramref name="key"/>.
    /// Returns true when at least one entity was found.
    /// Matching entities are written to <paramref name="destination"/> and the count is reported
    /// via <paramref name="written"/>.
    /// </summary>
    public bool TryGet(TComponent key, Span<Entity> destination, out int written)
    {
        written = 0;
        var query = _world.Query(in _scope);
        var chunks = query.GetChunks();

        foreach (var chunk in chunks)
        {
            var entities = chunk.GetEntities();
            var components = chunk.GetSpan<TComponent>();
            var count = chunk.Count;

            for (var i = 0; i < count; i++)
            {
                if (!components[i].Equals(key))
                    continue;

                if (written < destination.Length)
                    destination[written] = entities[i];
                written++;
            }
        }

        return written > 0;
    }

}
