using System.Threading;

namespace MiniArch.Core;

public sealed class Query
{
    private readonly World _world;
    private readonly QueryFilter _filter;
    private MatchingArchetypeSnapshot _matchingArchetypes = MatchingArchetypeSnapshot.Empty;
    private Signature? _requiredSignature;
    private Signature? _excludedSignature;
    private Signature? _anySignature;
    private int _refreshCount;

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

    public int RefreshCount => Volatile.Read(ref _refreshCount);

    public IReadOnlyList<Archetype> MatchedArchetypes
    {
        get
        {
            RefreshIfNeeded();
            return Volatile.Read(ref _matchingArchetypes).Archetypes;
        }
    }

    public ChunkEnumerable Chunks => new(this);

    internal Archetype[] EnsureMatchingArchetypes()
    {
        RefreshIfNeeded();
        return Volatile.Read(ref _matchingArchetypes).Archetypes;
    }

    private void RefreshIfNeeded()
    {
        var worldGeneration = _world.ArchetypeGeneration;
        while (true)
        {
            var snapshot = Volatile.Read(ref _matchingArchetypes);
            if (snapshot.WorldGeneration == worldGeneration)
            {
                return;
            }

            var matchingArchetypes = BuildMatchingArchetypes();
            var refreshed = new MatchingArchetypeSnapshot(worldGeneration, matchingArchetypes);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _matchingArchetypes, refreshed, snapshot), snapshot))
            {
                Interlocked.Increment(ref _refreshCount);
                return;
            }
        }
    }

    private Archetype[] BuildMatchingArchetypes()
    {
        var archetypes = _world.Archetypes;
        if (archetypes.Length == 0)
        {
            return Array.Empty<Archetype>();
        }

        var matchingArchetypes = new Archetype[archetypes.Length];
        var count = 0;
        foreach (var archetype in archetypes)
        {
            if (Matches(archetype))
            {
                matchingArchetypes[count++] = archetype;
            }
        }

        if (count == 0)
        {
            return Array.Empty<Archetype>();
        }

        if (count == matchingArchetypes.Length)
        {
            return matchingArchetypes;
        }

        var trimmed = new Archetype[count];
        Array.Copy(matchingArchetypes, trimmed, count);
        return trimmed;
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

    private sealed class MatchingArchetypeSnapshot
    {
        public static MatchingArchetypeSnapshot Empty { get; } = new(-1, Array.Empty<Archetype>());

        public MatchingArchetypeSnapshot(int worldGeneration, Archetype[] archetypes)
        {
            WorldGeneration = worldGeneration;
            Archetypes = archetypes;
        }

        public int WorldGeneration { get; }

        public Archetype[] Archetypes { get; }
    }
}
