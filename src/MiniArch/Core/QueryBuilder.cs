namespace MiniArch.Core;

/// <summary>
/// Fluent query filter builder.
/// </summary>
public readonly struct QueryBuilder
{
    private readonly World _world;
    private readonly QueryFilter _filter;

    internal QueryBuilder(World world, QueryFilter filter)
    {
        _world = world;
        _filter = filter;
    }

    /// <summary>
    /// Gets the required signature.
    /// </summary>
    public Signature RequiredSignature => _filter.Required.ToSignature();

    /// <summary>
    /// Gets the excluded signature.
    /// </summary>
    public Signature ExcludedSignature => _filter.Excluded.ToSignature();

    /// <summary>
    /// Gets the any-match signature.
    /// </summary>
    public Signature AnySignature => _filter.Any.ToSignature();

    /// <summary>
    /// Adds a required type.
    /// </summary>
    public QueryBuilder With<T>()
    {
        var componentType = _world.Components.GetOrCreate<T>();
        var required = _filter.Required.Add(componentType);
        if (required.Equals(_filter.Required))
        {
            return this;
        }

        return new QueryBuilder(_world, new QueryFilter(required, _filter.Excluded, _filter.Any));
    }

    /// <summary>
    /// Adds an excluded type.
    /// </summary>
    public QueryBuilder Without<T>()
    {
        var componentType = _world.Components.GetOrCreate<T>();
        var excluded = _filter.Excluded.Add(componentType);
        if (excluded.Equals(_filter.Excluded))
        {
            return this;
        }

        return new QueryBuilder(_world, new QueryFilter(_filter.Required, excluded, _filter.Any));
    }

    /// <summary>
    /// Adds an any-match type.
    /// </summary>
    public QueryBuilder Any<T>()
    {
        var componentType = _world.Components.GetOrCreate<T>();
        var any = _filter.Any.Add(componentType);
        if (any.Equals(_filter.Any))
        {
            return this;
        }

        return new QueryBuilder(_world, new QueryFilter(_filter.Required, _filter.Excluded, any));
    }

    /// <summary>
    /// Alias for <see cref="Any{T}()" />.
    /// </summary>
    public QueryBuilder Or<T>() => Any<T>();

    /// <summary>
    /// Builds the query.
    /// </summary>
    public Query Build() => _world.GetOrCreateQuery(_filter);

    /// <summary>
    /// Gets the matched archetypes.
    /// </summary>
    public IReadOnlyList<Archetype> MatchedArchetypes => Build().MatchedArchetypes;

    /// <summary>
    /// Gets the matching chunks.
    /// </summary>
    public ChunkEnumerable Chunks => Build().Chunks;
}
