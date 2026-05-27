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
    private readonly object _refreshLock = new();
    private Signature? _requiredSignature;
    private Signature? _excludedSignature;
    private Signature? _anySignature;
    private int _refreshCount;

    private volatile int _snapshotGeneration = -1;
    private Archetype[] _snapshotArchetypes = Array.Empty<Archetype>();
    private Chunk[] _snapshotChunks = Array.Empty<Chunk>();
    private Archetype[] _scratchArchetypes = Array.Empty<Archetype>();
    private Chunk[] _scratchChunks = Array.Empty<Chunk>();

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
            EnsureMatchingSnapshot();
            return Volatile.Read(ref _snapshotArchetypes);
        }
    }

    /// <summary>
    /// Gets the matched chunks.
    /// </summary>
    public IReadOnlyList<Chunk> MatchedChunks
    {
        get
        {
            EnsureMatchingSnapshot();
            return Volatile.Read(ref _snapshotChunks);
        }
    }

    /// <summary>
    /// Gets the matched chunks as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Chunk> GetChunkSpan()
    {
        EnsureMatchingSnapshot();
        return Volatile.Read(ref _snapshotChunks);
    }

    /// <summary>
    /// Gets a chunk enumerable.
    /// </summary>
    public ChunkEnumerable Chunks => new(this);

    internal Archetype[] EnsureMatchingArchetypes()
    {
        EnsureMatchingSnapshot();
        return Volatile.Read(ref _snapshotArchetypes);
    }

    internal Chunk[] EnsureMatchingChunks()
    {
        EnsureMatchingSnapshot();
        return Volatile.Read(ref _snapshotChunks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMatchingSnapshot()
    {
        if (_snapshotGeneration == _world.QueryGeneration)
        {
            return;
        }

        RefreshSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefreshSlow()
    {
        lock (_refreshLock)
        {
            var worldGen = _world.QueryGeneration;
            if (_snapshotGeneration == worldGen)
            {
                return;
            }

            BuildMatchingSnapshot(worldGen);
            _snapshotGeneration = worldGen;
            Interlocked.Increment(ref _refreshCount);
        }
    }

    private void BuildMatchingSnapshot(int worldQueryGeneration)
    {
        var archetypes = _world.Archetypes;
        if (archetypes.Length == 0)
        {
            SwapSnapshotToEmpty();
            return;
        }

        if (_scratchArchetypes.Length < archetypes.Length)
        {
            _scratchArchetypes = new Archetype[archetypes.Length];
        }

        var matchedArchetypeCount = 0;
        var matchedChunkCount = 0;
        foreach (var archetype in archetypes)
        {
            if (Matches(archetype))
            {
                _scratchArchetypes[matchedArchetypeCount++] = archetype;
                matchedChunkCount += archetype.Chunks.Count;
            }
        }

        if (matchedArchetypeCount == 0)
        {
            SwapSnapshotToEmpty();
            return;
        }

        if (_scratchArchetypes.Length != matchedArchetypeCount)
        {
            var trimmed = new Archetype[matchedArchetypeCount];
            Array.Copy(_scratchArchetypes, trimmed, matchedArchetypeCount);
            _scratchArchetypes = trimmed;
        }

        if (matchedChunkCount == 0)
        {
            _scratchChunks = Array.Empty<Chunk>();
            SwapSnapshot();
            return;
        }

        if (_scratchChunks.Length < matchedChunkCount)
        {
            _scratchChunks = new Chunk[matchedChunkCount];
        }

        var chunkIndex = 0;
        for (var archetypeIndex = 0; archetypeIndex < matchedArchetypeCount; archetypeIndex++)
        {
            var chunks = _scratchArchetypes[archetypeIndex].Chunks;
            for (var i = 0; i < chunks.Count; i++)
            {
                _scratchChunks[chunkIndex++] = chunks[i];
            }
        }

        SwapSnapshot();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapSnapshotToEmpty()
    {
        _scratchArchetypes = Array.Empty<Archetype>();
        _scratchChunks = Array.Empty<Chunk>();
        SwapSnapshot();
    }

    // Not atomic: readers may see new archetypes + old chunks (or vice versa)
    // across the two Volatile.Write calls. Safe under "no concurrent world writes" precondition.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapSnapshot()
    {
        var oldA = _snapshotArchetypes;
        var oldC = _snapshotChunks;
        Volatile.Write(ref _snapshotArchetypes, _scratchArchetypes);
        Volatile.Write(ref _snapshotChunks, _scratchChunks);
        _scratchArchetypes = oldA;
        _scratchChunks = oldC;
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
