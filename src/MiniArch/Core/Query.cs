using System.Runtime.CompilerServices;
using System.Threading;

namespace MiniArch.Core;

using MiniArch;

/// <summary>
/// Cached archetype and chunk query.
/// </summary>
internal sealed class Query
{
    private readonly World _world;
    private readonly QueryFilter _filter;
    private readonly object _refreshLock = new();
    private int _refreshCount;

    private Archetype[] _snapshotArchetypes = Array.Empty<Archetype>();
    private Chunk[] _snapshotChunks = Array.Empty<Chunk>();
    private ChunkView[] _snapshotChunkViews = [];
    private Archetype[] _scratchArchetypes = Array.Empty<Archetype>();
    private Chunk[] _scratchChunks = Array.Empty<Chunk>();
    private ChunkView[] _scratchChunkViews = [];
    private int _snapshotCount;
    private int _snapshotVersion = -1;

    internal Query(World world, QueryFilter filter)
    {
        _world = world;
        _filter = filter;
    }

    internal static Query Create(World world, in QueryDescription description)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.GetAdvancedQuery(in description);
    }

    internal Signature RequiredSignature => _filter.Required.ToSignature();
    internal Signature ExcludedSignature => _filter.Excluded.ToSignature();
    internal Signature AnySignature => _filter.Any.ToSignature();
    internal int RefreshCount => Volatile.Read(ref _refreshCount);

    internal IReadOnlyList<Archetype> MatchedArchetypes
    {
        get
        {
            EnsureRefreshed();
            return new ArraySegment<Archetype>(_snapshotArchetypes, 0, _snapshotCount);
        }
    }

    internal IReadOnlyList<Chunk> MatchedChunks
    {
        get
        {
            EnsureRefreshed();
            return new ArraySegment<Chunk>(_snapshotChunks, 0, _snapshotCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Chunk> GetChunkSpan()
    {
        EnsureRefreshed();
        return _snapshotChunks.AsSpan(0, _snapshotCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunk[] GetChunkArray(out int count)
    {
        EnsureRefreshed();
        count = _snapshotCount;
        return _snapshotChunks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<ChunkView> GetChunkViewSpan()
    {
        EnsureRefreshed();
        return _snapshotChunkViews.AsSpan(0, _snapshotCount);
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
    internal ChunkEnumerable Chunks => new(this);

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
            _scratchChunkViews = new ChunkView[count];
        }

        var idx = 0;
        for (var i = 0; i < archetypes.Length; i++)
        {
            var a = archetypes[i];
            if (Matches(a, requiredMask, excludedMask, anyMask))
            {
                _scratchArchetypes[idx] = a;
                _scratchChunks[idx] = a.GetChunkSpan()[0];
                _scratchChunkViews[idx] = new ChunkView(a);
                idx++;
            }
        }

        // Publish snapshot via swap (volatile ensures readers see consistent data).
        var oldA = _snapshotArchetypes;
        var oldC = _snapshotChunks;
        var oldCV = _snapshotChunkViews;
        Volatile.Write(ref _snapshotArchetypes, _scratchArchetypes);
        Volatile.Write(ref _snapshotChunks, _scratchChunks);
        Volatile.Write(ref _snapshotChunkViews, _scratchChunkViews);
        Volatile.Write(ref _snapshotCount, count);
        Volatile.Write(ref _snapshotVersion, version);
        _scratchArchetypes = oldA;
        _scratchChunks = oldC;
        _scratchChunkViews = oldCV;
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
