namespace MiniArch.Core;

public sealed class Query
{
    private readonly World _world;
    private readonly QueryFilter _filter;
    private readonly List<Archetype> _matchingArchetypes = new();
    private Signature? _requiredSignature;
    private Signature? _excludedSignature;
    private Signature? _anySignature;
    private int _cachedWorldGeneration = -1;

    internal Query(World world, QueryFilter filter)
    {
        _world = world;
        _filter = filter;
    }

    public Signature RequiredSignature => _requiredSignature ??= _filter.Required.ToSignature();

    public Signature ExcludedSignature => _excludedSignature ??= _filter.Excluded.ToSignature();

    public Signature AnySignature => _anySignature ??= _filter.Any.ToSignature();

    public Query With<T>()
    {
        var componentType = _world.Components.GetOrCreate<T>();
        var required = _filter.Required.Add(componentType);
        if (required.Equals(_filter.Required))
        {
            return this;
        }

        return _world.GetOrCreateQuery(new QueryFilter(required, _filter.Excluded, _filter.Any));
    }

    public Query Without<T>()
    {
        var componentType = _world.Components.GetOrCreate<T>();
        var excluded = _filter.Excluded.Add(componentType);
        if (excluded.Equals(_filter.Excluded))
        {
            return this;
        }

        return _world.GetOrCreateQuery(new QueryFilter(_filter.Required, excluded, _filter.Any));
    }

    public Query Any<T>()
    {
        var componentType = _world.Components.GetOrCreate<T>();
        var any = _filter.Any.Add(componentType);
        if (any.Equals(_filter.Any))
        {
            return this;
        }

        return _world.GetOrCreateQuery(new QueryFilter(_filter.Required, _filter.Excluded, any));
    }

    public Query Or<T>() => Any<T>();

    public int RefreshCount { get; private set; }

    public IReadOnlyList<Archetype> MatchedArchetypes
    {
        get
        {
            RefreshIfNeeded();
            return _matchingArchetypes;
        }
    }

    public ChunkEnumerable Chunks => new(this);

    internal IReadOnlyList<Archetype> EnsureMatchingArchetypes()
    {
        RefreshIfNeeded();
        return _matchingArchetypes;
    }

    private void RefreshIfNeeded()
    {
        if (_cachedWorldGeneration == _world.ArchetypeGeneration)
        {
            return;
        }

        _matchingArchetypes.Clear();
        foreach (var archetype in _world.Archetypes)
        {
            if (Matches(archetype))
            {
                _matchingArchetypes.Add(archetype);
            }
        }

        _cachedWorldGeneration = _world.ArchetypeGeneration;
        RefreshCount++;
    }

    private bool Matches(Archetype archetype)
    {
        var required = _filter.Required.AsSpan();
        for (var i = 0; i < required.Length; i++)
        {
            if (!archetype.Signature.Contains(required[i]))
            {
                return false;
            }
        }

        var excluded = _filter.Excluded.AsSpan();
        for (var i = 0; i < excluded.Length; i++)
        {
            if (archetype.Signature.Contains(excluded[i]))
            {
                return false;
            }
        }

        var any = _filter.Any.AsSpan();
        if (any.Length == 0)
        {
            return true;
        }

        for (var i = 0; i < any.Length; i++)
        {
            if (archetype.Signature.Contains(any[i]))
            {
                return true;
            }
        }

        return false;
    }
}
