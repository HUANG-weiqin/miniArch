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
    private Mask128 _requiredMask;
    private Mask128 _excludedMask;
    private Mask128 _anyMask;
    private bool _masksInitialized;
    private int _refreshCount;

    private Archetype[] _snapshotArchetypes = Array.Empty<Archetype>();
    private long[] _snapshotGenerations = Array.Empty<long>();
    private Chunk[] _snapshotChunks = Array.Empty<Chunk>();
    private Archetype[] _scratchArchetypes = Array.Empty<Archetype>();
    private Chunk[] _scratchChunks = Array.Empty<Chunk>();
    private long[] _scratchGenerations = Array.Empty<long>();
    private int _snapshotArchetypeCount;
    private int _chunksDirty = 1;
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
            EnsureMatchingArchetypes();
            var snapshot = ReadArchetypeSnapshot();
            return new ArraySegment<Archetype>(snapshot.Items, 0, snapshot.Count);
        }
    }

    /// <summary>
    /// Gets the matched chunks.
    /// </summary>
    public IReadOnlyList<Chunk> MatchedChunks
    {
        get
        {
            EnsureMatchingChunks();
            return Volatile.Read(ref _snapshotChunks);
        }
    }

    /// <summary>
    /// Gets the matched chunks as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Chunk> GetChunkSpan()
    {
        EnsureMatchingChunks();
        return Volatile.Read(ref _snapshotChunks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Archetype> GetArchetypeSpan()
    {
        EnsureMatchingArchetypes();
        var snapshot = ReadArchetypeSnapshot();
        return snapshot.Items.AsSpan(0, snapshot.Count);
    }

    /// <summary>
    /// Gets a chunk enumerable.
    /// </summary>
    public ChunkEnumerable Chunks => new(this);

    internal Chunk[] EnsureMatchingChunks()
    {
        EnsureMatchingArchetypes();
        EnsureChunkSnapshot();
        return Volatile.Read(ref _snapshotChunks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (Archetype[] Items, int Count) ReadArchetypeSnapshot()
    {
        while (true)
        {
            var items = Volatile.Read(ref _snapshotArchetypes);
            var count = Volatile.Read(ref _snapshotArchetypeCount);
            if ((uint)count <= (uint)items.Length)
            {
                return (items, count);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMatchingArchetypes()
    {
        if (!HasAnyArchetypeGenerationChanged())
        {
            return;
        }

        RefreshArchetypesSlow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureChunkSnapshot()
    {
        if (Volatile.Read(ref _chunksDirty) == 0)
        {
            return;
        }

        RefreshChunksSlow();
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

        var snapshot = ReadArchetypeSnapshot();
        var snapshotArchetypes = snapshot.Items;
        var snapshotGenerations = Volatile.Read(ref _snapshotGenerations);
        var snapshotArchetypeCount = snapshot.Count;

        if (snapshotGenerations.Length < snapshotArchetypeCount)
        {
            return true;
        }

        for (var i = 0; i < snapshotArchetypeCount; i++)
        {
            if (snapshotArchetypes[i].Generation != snapshotGenerations[i])
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefreshArchetypesSlow()
    {
        lock (_refreshLock)
        {
            if (!HasAnyArchetypeGenerationChanged())
            {
                return;
            }

            BuildMatchingArchetypeSnapshot();
            Interlocked.Increment(ref _refreshCount);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefreshChunksSlow()
    {
        lock (_refreshLock)
        {
            if (Volatile.Read(ref _chunksDirty) == 0)
            {
                return;
            }

            BuildChunkSnapshot();
        }
    }

    private void BuildMatchingArchetypeSnapshot()
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
            _scratchGenerations = new long[archetypes.Length];
        }

        var matchedArchetypeCount = 0;
        foreach (var archetype in archetypes)
        {
            if (Matches(archetype))
            {
                _scratchArchetypes[matchedArchetypeCount] = archetype;
                _scratchGenerations[matchedArchetypeCount] = archetype.Generation;
                matchedArchetypeCount++;
            }
        }

        if (matchedArchetypeCount == 0)
        {
            SwapSnapshotToEmpty();
            _initialized = true;
            return;
        }

        SwapArchetypeSnapshot(matchedArchetypeCount);
        Volatile.Write(ref _chunksDirty, 1);
        _initialized = true;
    }

    private void BuildChunkSnapshot()
    {
        var snapshot = ReadArchetypeSnapshot();
        var snapshotArchetypes = snapshot.Items;
        var snapshotArchetypeCount = snapshot.Count;
        if (snapshotArchetypeCount == 0)
        {
            Volatile.Write(ref _snapshotChunks, Array.Empty<Chunk>());
            Volatile.Write(ref _chunksDirty, 0);
            return;
        }

        var matchedChunkCount = 0;
        for (var archetypeIndex = 0; archetypeIndex < snapshotArchetypeCount; archetypeIndex++)
        {
            var chunks = snapshotArchetypes[archetypeIndex].Chunks;
            for (var chunkListIndex = 0; chunkListIndex < chunks.Count; chunkListIndex++)
            {
                if (chunks[chunkListIndex].Count > 0)
                {
                    matchedChunkCount++;
                }
            }
        }

        if (matchedChunkCount == 0)
        {
            Volatile.Write(ref _snapshotChunks, Array.Empty<Chunk>());
            // Still update generations so HasAnyArchetypeGenerationChanged doesn't
            // keep returning true, causing constant empty rebuilds.
            UpdateStoredGenerations(snapshotArchetypes, snapshotArchetypeCount);
            Volatile.Write(ref _chunksDirty, 0);
            return;
        }

        if (_scratchChunks.Length < matchedChunkCount)
        {
            _scratchChunks = new Chunk[matchedChunkCount];
        }

        var chunkIndex = 0;
        for (var archetypeIndex = 0; archetypeIndex < snapshotArchetypeCount; archetypeIndex++)
        {
            var chunks = snapshotArchetypes[archetypeIndex].Chunks;
            for (var i = 0; i < chunks.Count; i++)
            {
                if (chunks[i].Count > 0)
                {
                    _scratchChunks[chunkIndex++] = chunks[i];
                }
            }
        }

        if (_scratchChunks.Length != matchedChunkCount)
        {
            var trimmed = new Chunk[matchedChunkCount];
            Array.Copy(_scratchChunks, trimmed, matchedChunkCount);
            _scratchChunks = trimmed;
        }

        var old = _snapshotChunks;
        Volatile.Write(ref _snapshotChunks, _scratchChunks);
        _scratchChunks = old;

        // Update stored generations so HasAnyArchetypeGenerationChanged doesn't keep
        // returning true (which would cause constant chunk rebuilds every frame).
        UpdateStoredGenerations(snapshotArchetypes, snapshotArchetypeCount);

        Volatile.Write(ref _chunksDirty, 0);
    }

    private void UpdateStoredGenerations(Archetype[] archetypes, int count)
    {
        var gens = Volatile.Read(ref _snapshotGenerations);
        for (var i = 0; i < count && i < gens.Length; i++)
            gens[i] = archetypes[i].Generation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapSnapshotToEmpty()
    {
        _scratchArchetypes = Array.Empty<Archetype>();
        _scratchGenerations = Array.Empty<long>();
        SwapArchetypeSnapshot(0);
        Volatile.Write(ref _snapshotChunks, Array.Empty<Chunk>());
        Volatile.Write(ref _chunksDirty, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapArchetypeSnapshot(int count)
    {
        var oldA = _snapshotArchetypes;
        var oldG = _snapshotGenerations;
        Volatile.Write(ref _snapshotArchetypes, _scratchArchetypes);
        Volatile.Write(ref _snapshotGenerations, _scratchGenerations);
        Volatile.Write(ref _snapshotArchetypeCount, count);
        _scratchArchetypes = oldA;
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
    private static Mask128 ComputeFilterMask(ReadOnlySpan<ComponentType> components)
    {
        ulong low = 0;
        ulong high = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var id = components[i].Value;
            if ((uint)id < 64)
            {
                low |= 1UL << id;
            }
            else if ((uint)id < 128)
            {
                high |= 1UL << (id - 64);
            }
        }

        return new Mask128(low, high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(Archetype archetype)
    {
        EnsureMasksInitialized();
        var archMask = archetype.Signature.ComponentMask;

            if (!_requiredMask.IsZero())
            {
                if ((archMask.Low & _requiredMask.Low) != _requiredMask.Low)
                    return false;
                if (_requiredMask.HasHighBits && (archMask.High & _requiredMask.High) != _requiredMask.High)
                    return false;
            }

            if (!_excludedMask.IsZero())
            {
                if ((archMask.Low & _excludedMask.Low) != 0)
                    return false;
                if (_excludedMask.HasHighBits && (archMask.High & _excludedMask.High) != 0)
                    return false;
            }

        return MatchesSlow(archetype, archMask);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool MatchesSlow(Archetype archetype, Mask128 archMask)
    {
        // Only check component ids >= 128 (not covered by mask)
        var required = _filter.Required.AsSpan();
        for (var i = 0; i < required.Length; i++)
        {
            var component = required[i];
            if ((uint)component.Value >= 128 && !archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        var excluded = _filter.Excluded.AsSpan();
        for (var i = 0; i < excluded.Length; i++)
        {
            var component = excluded[i];
            if ((uint)component.Value >= 128 && archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        var any = _filter.Any.AsSpan();
        if (any.Length == 0)
        {
            return true;
        }

        // Fast any-check via 128-bit mask
            if (!_anyMask.IsZero())
            {
                if ((archMask.Low & _anyMask.Low) != 0)
                    return true;
                if (_anyMask.HasHighBits && (archMask.High & _anyMask.High) != 0)
                    return true;
            }

        // Slow any-check for ids >= 128
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
