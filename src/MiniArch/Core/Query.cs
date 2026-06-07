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

    private Archetype[] _snapshotArchetypes = Array.Empty<Archetype>();
    private Chunk[] _snapshotChunks = Array.Empty<Chunk>();
    private Archetype[] _scratchArchetypes = Array.Empty<Archetype>();
    private Chunk[] _scratchChunks = Array.Empty<Chunk>();
    private int _snapshotCount;
    private int _snapshotVersion = -1;

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
            EnsureRefreshed();
            return new ArraySegment<Archetype>(_snapshotArchetypes, 0, _snapshotCount);
        }
    }

    /// <summary>
    /// Gets the matched chunks.
    /// </summary>
    public IReadOnlyList<Chunk> MatchedChunks
    {
        get
        {
            EnsureRefreshed();
            return new ArraySegment<Chunk>(_snapshotChunks, 0, _snapshotCount);
        }
    }

    /// <summary>
    /// Gets the matched chunks as a span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<Chunk> GetChunkSpan()
    {
        EnsureRefreshed();
        return _snapshotChunks.AsSpan(0, _snapshotCount);
    }

    /// <summary>
    /// Gets the matched chunks as an array plus active count (zero allocation, returns cached array).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunk[] GetChunkArray(out int count)
    {
        EnsureRefreshed();
        count = _snapshotCount;
        return _snapshotChunks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Archetype> GetArchetypeSpan()
    {
        EnsureRefreshed();
        return _snapshotArchetypes.AsSpan(0, _snapshotCount);
    }

    /// <summary>
    /// Gets a chunk enumerable.
    /// </summary>
    public ChunkEnumerable Chunks => new(this);

    /// <summary>
    /// Typed chunk enumeration for a single component type. Pre-computes column
    /// indices per archetype, eliminating per-chunk lookups.
    /// </summary>
    public ChunkViewEnumerable<T> ChunksOf<T>() where T : struct => new(this);

    /// <summary>
    /// Typed chunk enumeration for two component types.
    /// </summary>
    public ChunkViewEnumerable<T1, T2> ChunksOf<T1, T2>()
        where T1 : struct where T2 : struct => new(this);

    /// <summary>
    /// Typed chunk enumeration for three component types.
    /// </summary>
    public ChunkViewEnumerable<T1, T2, T3> ChunksOf<T1, T2, T3>()
        where T1 : struct where T2 : struct where T3 : struct => new(this);

    /// <summary>
    /// Typed chunk enumeration for four component types.
    /// </summary>
    public ChunkViewEnumerable<T1, T2, T3, T4> ChunksOf<T1, T2, T3, T4>()
        where T1 : struct where T2 : struct where T3 : struct where T4 : struct => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRefreshed()
    {
        if (_world.ArchetypeVersion != _snapshotVersion)
        {
            Refresh();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refresh()
    {
        lock (_refreshLock)
        {
            if (_world.ArchetypeVersion == _snapshotVersion)
            {
                return;
            }

            BuildSnapshot();
            Interlocked.Increment(ref _refreshCount);
        }
    }

    private void BuildSnapshot()
    {
        var archetypes = _world.Archetypes;
        var version = _world.ArchetypeVersion;

        // Compute match masks once per refresh.
        var requiredMask = ComputeFilterMask(_filter.Required.AsSpan());
        var excludedMask = ComputeFilterMask(_filter.Excluded.AsSpan());
        var anyMask = ComputeFilterMask(_filter.Any.AsSpan());

        var count = 0;
        for (var i = 0; i < archetypes.Length; i++)
        {
            if (Matches(archetypes[i], requiredMask, excludedMask, anyMask))
            {
                count++;
            }
        }

        if (_scratchArchetypes.Length < count)
        {
            _scratchArchetypes = new Archetype[count];
            _scratchChunks = new Chunk[count];
        }

        var idx = 0;
        for (var i = 0; i < archetypes.Length; i++)
        {
            var a = archetypes[i];
            if (Matches(a, requiredMask, excludedMask, anyMask))
            {
                _scratchArchetypes[idx] = a;
                _scratchChunks[idx] = a.GetChunkSpan()[0];
                idx++;
            }
        }

        // Publish snapshot via swap (volatile ensures readers see consistent data).
        var oldA = _snapshotArchetypes;
        var oldC = _snapshotChunks;
        Volatile.Write(ref _snapshotArchetypes, _scratchArchetypes);
        Volatile.Write(ref _snapshotChunks, _scratchChunks);
        Volatile.Write(ref _snapshotCount, count);
        Volatile.Write(ref _snapshotVersion, version);
        _scratchArchetypes = oldA;
        _scratchChunks = oldC;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ComponentMask ComputeFilterMask(ReadOnlySpan<ComponentType> components)
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var id = components[i].Value;
            if ((uint)id < 64)
            {
                b0 |= 1UL << id;
            }
            else if ((uint)id < 128)
            {
                b1 |= 1UL << (id - 64);
            }
            else if ((uint)id < 192)
            {
                b2 |= 1UL << (id - 128);
            }
            else if ((uint)id < 256)
            {
                b3 |= 1UL << (id - 192);
            }
        }

        return new ComponentMask(b0, b1, b2, b3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(
        Archetype archetype,
        ComponentMask requiredMask,
        ComponentMask excludedMask,
        ComponentMask anyMask)
    {
        var archMask = archetype.Signature.ComponentMask;

        if (!requiredMask.IsZero())
        {
            if ((archMask.B0 & requiredMask.B0) != requiredMask.B0)
                return false;
            if (requiredMask.HasB1 && (archMask.B1 & requiredMask.B1) != requiredMask.B1)
                return false;
            if (requiredMask.HasB2 && (archMask.B2 & requiredMask.B2) != requiredMask.B2)
                return false;
            if (requiredMask.HasB3 && (archMask.B3 & requiredMask.B3) != requiredMask.B3)
                return false;
        }

        if (!excludedMask.IsZero())
        {
            if ((archMask.B0 & excludedMask.B0) != 0)
                return false;
            if (excludedMask.HasB1 && (archMask.B1 & excludedMask.B1) != 0)
                return false;
            if (excludedMask.HasB2 && (archMask.B2 & excludedMask.B2) != 0)
                return false;
            if (excludedMask.HasB3 && (archMask.B3 & excludedMask.B3) != 0)
                return false;
        }

        // Fallback: check components with IDs >= 256 (not covered by 256-bit mask)
        var required = _filter.Required.AsSpan();
        for (var i = 0; i < required.Length; i++)
        {
            var component = required[i];
            if ((uint)component.Value >= 256 && !archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        var excluded = _filter.Excluded.AsSpan();
        for (var i = 0; i < excluded.Length; i++)
        {
            var component = excluded[i];
            if ((uint)component.Value >= 256 && archetype.Signature.Contains(component))
            {
                return false;
            }
        }

        var any = _filter.Any.AsSpan();
        if (any.Length == 0)
        {
            return true;
        }

        // Fast any-check via 256-bit mask
        if (!anyMask.IsZero())
        {
            if ((archMask.B0 & anyMask.B0) != 0)
                return true;
            if (anyMask.HasB1 && (archMask.B1 & anyMask.B1) != 0)
                return true;
            if (anyMask.HasB2 && (archMask.B2 & anyMask.B2) != 0)
                return true;
            if (anyMask.HasB3 && (archMask.B3 & anyMask.B3) != 0)
                return true;
        }

        // Slow any-check for ids >= 256
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
