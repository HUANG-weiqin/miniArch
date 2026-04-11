namespace MiniArch.Core;

public readonly struct QueryBuilder
{
    private readonly World _world;
    private readonly QueryFilter _filter;

    internal QueryBuilder(World world, QueryFilter filter)
    {
        _world = world;
        _filter = filter;
    }

    public Signature RequiredSignature => _filter.Required.ToSignature();

    public Signature ExcludedSignature => _filter.Excluded.ToSignature();

    public Signature AnySignature => _filter.Any.ToSignature();

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

    public QueryBuilder Or<T>() => Any<T>();

    public Query Build() => _world.GetOrCreateQuery(_filter);

    public IReadOnlyList<Archetype> MatchedArchetypes => Build().MatchedArchetypes;

    public ChunkEnumerable Chunks => Build().Chunks;
}
