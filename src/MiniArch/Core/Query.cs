using System.Runtime.CompilerServices;
using System.Threading;

namespace MiniArch.Core;

/// <summary>
/// Cached archetype and chunk query.
/// </summary>
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

    /// <summary>
    /// Creates or reuses an advanced query for a world and description.
    /// </summary>
    public static Query Create(World world, in QueryDescription description)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.GetAdvancedQuery(in description);
    }

    /// <summary>
    /// Gets the required signature.
    /// </summary>
    public Signature RequiredSignature => _requiredSignature ??= _filter.Required.ToSignature();

    /// <summary>
    /// Gets the excluded signature.
    /// </summary>
    public Signature ExcludedSignature => _excludedSignature ??= _filter.Excluded.ToSignature();

    /// <summary>
    /// Gets the any-match signature.
    /// </summary>
    public Signature AnySignature => _anySignature ??= _filter.Any.ToSignature();

    /// <summary>
    /// Gets the refresh count.
    /// </summary>
    public int RefreshCount => Volatile.Read(ref _refreshCount);

    /// <summary>
    /// Gets the matched archetypes.
    /// </summary>
    public IReadOnlyList<Archetype> MatchedArchetypes
    {
        get
        {
            return EnsureMatchingArchetypes();
        }
    }

    /// <summary>
    /// Gets the matched chunks.
    /// </summary>
    public IReadOnlyList<Chunk> MatchedChunks
    {
        get
        {
            return EnsureMatchingChunks();
        }
    }

    /// <summary>
    /// Gets the matched chunks as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Chunk> GetChunkSpan()
    {
        return EnsureMatchingSnapshot().Chunks;
    }

    /// <summary>
    /// Gets a chunk enumerable.
    /// </summary>
    public ChunkEnumerable Chunks => new(this);

    internal Archetype[] EnsureMatchingArchetypes()
    {
        return EnsureMatchingSnapshot().Archetypes;
    }

    internal Chunk[] EnsureMatchingChunks()
    {
        return EnsureMatchingSnapshot().Chunks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MatchingSnapshot EnsureMatchingSnapshot()
    {
        var snapshot = Volatile.Read(ref _matching);
        var archetypeGeneration = _world.ArchetypeGeneration;
        var layoutGeneration = _world.QueryLayoutGeneration;
        if (snapshot.ArchetypeGeneration == archetypeGeneration
            && snapshot.LayoutGeneration == layoutGeneration)
        {
            return snapshot;
        }

        return RefreshSlow(archetypeGeneration, layoutGeneration, snapshot);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private MatchingSnapshot RefreshSlow(int archetypeGeneration, int layoutGeneration, MatchingSnapshot snapshot)
    {
        while (true)
        {
            var refreshed = BuildMatchingSnapshot(archetypeGeneration, layoutGeneration);
            var observed = Interlocked.CompareExchange(ref _matching, refreshed, snapshot);
            if (ReferenceEquals(observed, snapshot))
            {
                Interlocked.Increment(ref _refreshCount);
                return refreshed;
            }

            snapshot = observed;
            if (snapshot.ArchetypeGeneration == archetypeGeneration
                && snapshot.LayoutGeneration == layoutGeneration)
            {
                return snapshot;
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
