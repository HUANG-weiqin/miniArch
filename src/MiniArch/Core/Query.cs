using System.Threading;

namespace MiniArch.Core;

public sealed class Query
{
    private readonly World _world;
    private readonly QueryFilter _filter;
    private MatchingSnapshot _matching = MatchingSnapshot.Empty;
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
            return Volatile.Read(ref _matching).Archetypes;
        }
    }

    public IReadOnlyList<Chunk> MatchedChunks
    {
        get
        {
            RefreshIfNeeded();
            return Volatile.Read(ref _matching).Chunks;
        }
    }

    public ChunkEnumerable Chunks => new(this);

    internal Archetype[] EnsureMatchingArchetypes()
    {
        RefreshIfNeeded();
        return Volatile.Read(ref _matching).Archetypes;
    }

    internal Chunk[] EnsureMatchingChunks()
    {
        RefreshIfNeeded();
        return Volatile.Read(ref _matching).Chunks;
    }

    private void RefreshIfNeeded()
    {
        var archetypeGeneration = _world.ArchetypeGeneration;
        var layoutGeneration = _world.QueryLayoutGeneration;
        while (true)
        {
            var snapshot = Volatile.Read(ref _matching);
            if (snapshot.ArchetypeGeneration == archetypeGeneration
                && snapshot.LayoutGeneration == layoutGeneration)
            {
                return;
            }

            var refreshed = BuildMatchingSnapshot(archetypeGeneration, layoutGeneration);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _matching, refreshed, snapshot), snapshot))
            {
                Interlocked.Increment(ref _refreshCount);
                return;
            }
        }
    }

    private MatchingSnapshot BuildMatchingSnapshot(int archetypeGeneration, int layoutGeneration)
    {
        var archetypes = _world.Archetypes;
        if (archetypes.Length == 0)
        {
            return new MatchingSnapshot(archetypeGeneration, layoutGeneration, Array.Empty<Archetype>(), Array.Empty<Chunk>());
        }

        var matchingArchetypes = new Archetype[archetypes.Length];
        var matchedArchetypeCount = 0;
        var matchedChunkCount = 0;
        foreach (var archetype in archetypes)
        {
            if (Matches(archetype))
            {
                matchingArchetypes[matchedArchetypeCount++] = archetype;
                matchedChunkCount += archetype.Chunks.Count;
            }
        }

        if (matchedArchetypeCount == 0)
        {
            return new MatchingSnapshot(archetypeGeneration, layoutGeneration, Array.Empty<Archetype>(), Array.Empty<Chunk>());
        }

        Archetype[] trimmedArchetypes;
        if (matchedArchetypeCount == matchingArchetypes.Length)
        {
            trimmedArchetypes = matchingArchetypes;
        }
        else
        {
            trimmedArchetypes = new Archetype[matchedArchetypeCount];
            Array.Copy(matchingArchetypes, trimmedArchetypes, matchedArchetypeCount);
        }

        if (matchedChunkCount == 0)
        {
            return new MatchingSnapshot(archetypeGeneration, layoutGeneration, trimmedArchetypes, Array.Empty<Chunk>());
        }

        var matchingChunks = new Chunk[matchedChunkCount];
        var chunkIndex = 0;
        for (var archetypeIndex = 0; archetypeIndex < trimmedArchetypes.Length; archetypeIndex++)
        {
            var chunks = trimmedArchetypes[archetypeIndex].Chunks;
            for (var i = 0; i < chunks.Count; i++)
            {
                matchingChunks[chunkIndex++] = chunks[i];
            }
        }

        return new MatchingSnapshot(archetypeGeneration, layoutGeneration, trimmedArchetypes, matchingChunks);
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

    private sealed class MatchingSnapshot
    {
        public static MatchingSnapshot Empty { get; } = new(-1, -1, Array.Empty<Archetype>(), Array.Empty<Chunk>());

        public MatchingSnapshot(int archetypeGeneration, int layoutGeneration, Archetype[] archetypes, Chunk[] chunks)
        {
            ArchetypeGeneration = archetypeGeneration;
            LayoutGeneration = layoutGeneration;
            Archetypes = archetypes;
            Chunks = chunks;
        }

        public int ArchetypeGeneration { get; }

        public int LayoutGeneration { get; }

        public Archetype[] Archetypes { get; }

        public Chunk[] Chunks { get; }
    }
}
