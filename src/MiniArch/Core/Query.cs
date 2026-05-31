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
    private long _requiredMask;
    private long _excludedMask;
    private long _anyMask;
    private bool _masksInitialized;
    private int _refreshCount;

    private Archetype[] _snapshotArchetypes = Array.Empty<Archetype>();
    private int[] _snapshotGenerations = Array.Empty<int>();
    private Chunk[] _snapshotChunks = Array.Empty<Chunk>();
    private Archetype[] _scratchArchetypes = Array.Empty<Archetype>();
    private Chunk[] _scratchChunks = Array.Empty<Chunk>();
    private int[] _scratchGenerations = Array.Empty<int>();
    private bool _initialized;
    private int _snapshotArchetypeVersion;

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

    internal Chunk[] EnsureMatchingChunks()
    {
        EnsureMatchingSnapshot();
        return Volatile.Read(ref _snapshotChunks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMatchingSnapshot()
    {
        if (!HasAnyArchetypeGenerationChanged())
        {
            return;
        }

        RefreshSlow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasAnyArchetypeGenerationChanged()
    {
        if (!_initialized)
        {
            return true;
        }

        // Check if new archetypes were created
        if (_world.ArchetypeVersion != _snapshotArchetypeVersion)
        {
            return true;
        }

        var snapshotArchetypes = Volatile.Read(ref _snapshotArchetypes);
        var snapshotGenerations = Volatile.Read(ref _snapshotGenerations);

        if (snapshotArchetypes.Length != snapshotGenerations.Length)
        {
            return true;
        }

        for (var i = 0; i < snapshotArchetypes.Length; i++)
        {
            if (snapshotArchetypes[i].Generation != snapshotGenerations[i])
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefreshSlow()
    {
        lock (_refreshLock)
        {
            if (!HasAnyArchetypeGenerationChanged())
            {
                return;
            }

            BuildMatchingSnapshot();
            Interlocked.Increment(ref _refreshCount);
        }
    }

    private void BuildMatchingSnapshot()
    {
        var archetypes = _world.Archetypes;
        _snapshotArchetypeVersion = _world.ArchetypeVersion;

        if (archetypes.Length == 0)
        {
            SwapSnapshotToEmpty();
            _initialized = true;
            return;
        }

        if (_scratchArchetypes.Length < archetypes.Length)
        {
            _scratchArchetypes = new Archetype[archetypes.Length];
            _scratchGenerations = new int[archetypes.Length];
        }

        var matchedArchetypeCount = 0;
        var matchedChunkCount = 0;
        foreach (var archetype in archetypes)
        {
            if (Matches(archetype))
            {
                _scratchArchetypes[matchedArchetypeCount] = archetype;
                _scratchGenerations[matchedArchetypeCount] = archetype.Generation;
                matchedArchetypeCount++;
                matchedChunkCount += archetype.Chunks.Count;
            }
        }

        if (matchedArchetypeCount == 0)
        {
            SwapSnapshotToEmpty();
            _initialized = true;
            return;
        }

        if (_scratchArchetypes.Length != matchedArchetypeCount)
        {
            var trimmedA = new Archetype[matchedArchetypeCount];
            var trimmedG = new int[matchedArchetypeCount];
            Array.Copy(_scratchArchetypes, trimmedA, matchedArchetypeCount);
            Array.Copy(_scratchGenerations, trimmedG, matchedArchetypeCount);
            _scratchArchetypes = trimmedA;
            _scratchGenerations = trimmedG;
        }

        if (matchedChunkCount == 0)
        {
            _scratchChunks = Array.Empty<Chunk>();
            SwapSnapshot();
            _initialized = true;
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
        _initialized = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapSnapshotToEmpty()
    {
        _scratchArchetypes = Array.Empty<Archetype>();
        _scratchChunks = Array.Empty<Chunk>();
        _scratchGenerations = Array.Empty<int>();
        SwapSnapshot();
    }

    // Not atomic: readers may see new archetypes + old chunks (or vice versa)
    // across the two Volatile.Write calls. Safe under "no concurrent world writes" precondition.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapSnapshot()
    {
        var oldA = _snapshotArchetypes;
        var oldC = _snapshotChunks;
        var oldG = _snapshotGenerations;
        Volatile.Write(ref _snapshotArchetypes, _scratchArchetypes);
        Volatile.Write(ref _snapshotChunks, _scratchChunks);
        Volatile.Write(ref _snapshotGenerations, _scratchGenerations);
        _scratchArchetypes = oldA;
        _scratchChunks = oldC;
        _scratchGenerations = oldG;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMasksInitialized()
    {
        if (!_masksInitialized)
        {
            InitializeMasks();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InitializeMasks()
    {
        _requiredMask = ComputeFilterMask(_filter.Required.AsSpan());
        _excludedMask = ComputeFilterMask(_filter.Excluded.AsSpan());
        _anyMask = ComputeFilterMask(_filter.Any.AsSpan());
        _masksInitialized = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeFilterMask(ReadOnlySpan<ComponentType> components)
    {
        long mask = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var id = components[i].Value;
            if ((uint)id < 64)
            {
                mask |= 1L << id;
            }
        }

        return mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(Archetype archetype)
    {
        EnsureMasksInitialized();
        var archMask = archetype.Signature.ComponentMask;

        if (_requiredMask != 0 && (archMask & _requiredMask) != _requiredMask)
        {
            return false;
        }

        if (_excludedMask != 0 && (archMask & _excludedMask) != 0)
        {
            return false;
        }

        return MatchesSlow(archetype, archMask);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool MatchesSlow(Archetype archetype, long archMask)
    {
        var required = _filter.Required.AsSpan();
        for (var i = 0; i < required.Length; i++)
        {
            var component = required[i];
            if ((uint)component.Value >= 64 && !archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        var excluded = _filter.Excluded.AsSpan();
        for (var i = 0; i < excluded.Length; i++)
        {
            var component = excluded[i];
            if ((uint)component.Value >= 64 && archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        var any = _filter.Any.AsSpan();
        if (any.Length == 0)
        {
            return true;
        }

        if (_anyMask != 0 && (archMask & _anyMask) != 0)
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
