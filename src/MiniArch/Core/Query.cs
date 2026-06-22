using System.Runtime.CompilerServices;
using System.Threading;

namespace MiniArch.Core;

using MiniArch;

/// <summary>
/// Cached archetype query with incremental append-only invalidation.
/// Archetypes are never removed, so we only scan newly added ones.
/// </summary>
internal sealed class Query
{
    private readonly World _world;
    private readonly QueryFilter _filter;
    private readonly object _refreshLock = new();
    private int _refreshCount;

    // Pre-computed filter masks (immutable after construction).
    private readonly ComponentMask _requiredMask;
    private readonly ComponentMask _excludedMask;
    private readonly ComponentMask _anyMask;

    // Unique matched archetypes (no duplicates, for entity enumeration).
    private Archetype[] _snapshotArchetypes = [];
    private int _matchedArchetypeCount;

    // One ChunkView per segment (for chunk iteration).
    private ChunkView[] _snapshotChunkViews = [];
    private int _chunkViewCount;

    private int _lastArchetypeCount = -1;

    // Tracks expected views per archetype (indexed parallel to _snapshotArchetypes).
    private int[] _archetypeExpectedViews = [];

    internal Query(World world, QueryFilter filter)
    {
        _world = world;
        _filter = filter;
        _requiredMask = ComputeFilterMask(filter.Required.AsSpan());
        _excludedMask = ComputeFilterMask(filter.Excluded.AsSpan());
        _anyMask = ComputeFilterMask(filter.Any.AsSpan());
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
            return new ArraySegment<Archetype>(_snapshotArchetypes, 0, _matchedArchetypeCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Archetype> GetArchetypeSpan()
    {
        EnsureRefreshed();
        return _snapshotArchetypes.AsSpan(0, _matchedArchetypeCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype[] GetArchetypeArray(out int count)
    {
        EnsureRefreshed();
        count = _matchedArchetypeCount;
        return _snapshotArchetypes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<ChunkView> GetChunkViewSpan()
    {
        EnsureRefreshed();
        return _snapshotChunkViews.AsSpan(0, _chunkViewCount);
    }

    /// <summary>
    /// Returns the underlying chunk-view snapshot array and live count.
    /// The array length may exceed <paramref name="count"/>; consumers must bound by <paramref name="count"/>.
    /// Used by the public <c>MiniArch.Query.ForEachChunkParallel</c> to avoid span capture in lambdas (zero-alloc per call).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkView[] GetChunkViewArray(out int count)
    {
        EnsureRefreshed();
        count = _chunkViewCount;
        return _snapshotChunkViews;
    }

    /// <summary>
    /// Gets an archetype enumerable.
    /// </summary>
    internal ArchetypeEnumerable Chunks => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRefreshed()
    {
        var currentCount = _world.ArchetypeCount;
        if (currentCount != _lastArchetypeCount)
        {
            Refresh();
            return;
        }

        // Check if any matched archetype changed segment count (chunked growth).
        if (_matchedArchetypeCount > 0)
        {
            for (var i = 0; i < _matchedArchetypeCount; i++)
            {
                var arch = _snapshotArchetypes[i];
                var expected = arch.IsChunked ? arch.SegmentCount : 1;
                if (expected != _archetypeExpectedViews[i])
                {
                    Refresh();
                    return;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refresh()
    {
        lock (_refreshLock)
        {
            AppendNewArchetypes(_world.ArchetypeCount);
            Interlocked.Increment(ref _refreshCount);
        }
    }

    private void AppendNewArchetypes(int currentArchetypeCount)
    {
        var archetypes = _world.Archetypes;
        var start = _lastArchetypeCount < 0 ? 0 : _lastArchetypeCount;

        // ── Full rebuild (count views + unique archetypes) ──
        // Count total views and unique archetypes.
        var totalViews = 0;
        var uniqueCount = 0;
        for (var i = 0; i < currentArchetypeCount; i++)
        {
            if (Matches(archetypes[i]))
            {
                var a = archetypes[i];
                totalViews += a.IsChunked ? a.SegmentCount : 1;
                uniqueCount++;
            }
        }

        // Grow arrays.
        if (_snapshotChunkViews.Length < totalViews)
        {
            var newLen = Math.Max(totalViews, _snapshotChunkViews.Length == 0 ? 4 : _snapshotChunkViews.Length * 2);
            Array.Resize(ref _snapshotChunkViews, newLen);
        }
        if (_snapshotArchetypes.Length < uniqueCount)
        {
            var newLen = Math.Max(uniqueCount, _snapshotArchetypes.Length == 0 ? 4 : _snapshotArchetypes.Length * 2);
            Array.Resize(ref _snapshotArchetypes, newLen);
        }
        if (_archetypeExpectedViews.Length < uniqueCount)
            Array.Resize(ref _archetypeExpectedViews, Math.Max(uniqueCount, _archetypeExpectedViews.Length == 0 ? 4 : _archetypeExpectedViews.Length * 2));

        // Populate unique archetypes + chunk views.
        var ai = 0;
        var ci = 0;
        for (var i = 0; i < currentArchetypeCount; i++)
        {
            var a = archetypes[i];
            if (!Matches(a)) continue;

            _snapshotArchetypes[ai] = a;
            var viewCount = a.IsChunked ? a.SegmentCount : 1;
            _archetypeExpectedViews[ai] = viewCount;

            if (a.IsChunked)
            {
                for (var s = 0; s < a.SegmentCount; s++)
                    _snapshotChunkViews[ci++] = new ChunkView(a, s);
            }
            else
            {
                _snapshotChunkViews[ci++] = new ChunkView(a);
            }
            ai++;
        }

        Volatile.Write(ref _chunkViewCount, ci);
        Volatile.Write(ref _matchedArchetypeCount, ai);
        Volatile.Write(ref _lastArchetypeCount, currentArchetypeCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(Archetype archetype)
    {
        var archMask = archetype.Signature.ComponentMask;

        // Required: archetype must have all required components.
        if (!_requiredMask.IsZero())
        {
            if ((archMask.B0 & _requiredMask.B0) != _requiredMask.B0)
                return false;
            if (_requiredMask.HasB1 && (archMask.B1 & _requiredMask.B1) != _requiredMask.B1)
                return false;
            if (_requiredMask.HasB2 && (archMask.B2 & _requiredMask.B2) != _requiredMask.B2)
                return false;
            if (_requiredMask.HasB3 && (archMask.B3 & _requiredMask.B3) != _requiredMask.B3)
                return false;
            if (_requiredMask.HasB4 && (archMask.B4 & _requiredMask.B4) != _requiredMask.B4)
                return false;
            if (_requiredMask.HasB5 && (archMask.B5 & _requiredMask.B5) != _requiredMask.B5)
                return false;
            if (_requiredMask.HasB6 && (archMask.B6 & _requiredMask.B6) != _requiredMask.B6)
                return false;
            if (_requiredMask.HasB7 && (archMask.B7 & _requiredMask.B7) != _requiredMask.B7)
                return false;
        }

        // Excluded: archetype must have none of the excluded components.
        if (!_excludedMask.IsZero())
        {
            if ((archMask.B0 & _excludedMask.B0) != 0)
                return false;
            if (_excludedMask.HasB1 && (archMask.B1 & _excludedMask.B1) != 0)
                return false;
            if (_excludedMask.HasB2 && (archMask.B2 & _excludedMask.B2) != 0)
                return false;
            if (_excludedMask.HasB3 && (archMask.B3 & _excludedMask.B3) != 0)
                return false;
            if (_excludedMask.HasB4 && (archMask.B4 & _excludedMask.B4) != 0)
                return false;
            if (_excludedMask.HasB5 && (archMask.B5 & _excludedMask.B5) != 0)
                return false;
            if (_excludedMask.HasB6 && (archMask.B6 & _excludedMask.B6) != 0)
                return false;
            if (_excludedMask.HasB7 && (archMask.B7 & _excludedMask.B7) != 0)
                return false;
        }

        // Fallback: check components with IDs >= 512 (not covered by 512-bit mask).
        var required = _filter.Required.AsSpan();
        for (var i = 0; i < required.Length; i++)
        {
            if ((uint)required[i].Value >= 512 && !archetype.Signature.Contains(required[i]))
                return false;
        }

        var excluded = _filter.Excluded.AsSpan();
        for (var i = 0; i < excluded.Length; i++)
        {
            if ((uint)excluded[i].Value >= 512 && archetype.Signature.Contains(excluded[i]))
                return false;
        }

        // Any: if no "any" filter, it's a match.
        var any = _filter.Any.AsSpan();
        if (any.Length == 0)
            return true;

        // Fast any-check via 512-bit mask.
        if (!_anyMask.IsZero())
        {
            if ((archMask.B0 & _anyMask.B0) != 0)
                return true;
            if (_anyMask.HasB1 && (archMask.B1 & _anyMask.B1) != 0)
                return true;
            if (_anyMask.HasB2 && (archMask.B2 & _anyMask.B2) != 0)
                return true;
            if (_anyMask.HasB3 && (archMask.B3 & _anyMask.B3) != 0)
                return true;
            if (_anyMask.HasB4 && (archMask.B4 & _anyMask.B4) != 0)
                return true;
            if (_anyMask.HasB5 && (archMask.B5 & _anyMask.B5) != 0)
                return true;
            if (_anyMask.HasB6 && (archMask.B6 & _anyMask.B6) != 0)
                return true;
            if (_anyMask.HasB7 && (archMask.B7 & _anyMask.B7) != 0)
                return true;
        }

        // Slow any-check for ids >= 512.
        for (var i = 0; i < any.Length; i++)
        {
            if (archetype.Signature.Contains(any[i]))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ComponentMask ComputeFilterMask(ReadOnlySpan<ComponentType> components)
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, b7 = 0;
        for (var i = 0; i < components.Length; i++)
        {
            var id = components[i].Value;
            if ((uint)id < 64)            b0 |= 1UL << id;
            else if ((uint)id < 128)      b1 |= 1UL << (id - 64);
            else if ((uint)id < 192)      b2 |= 1UL << (id - 128);
            else if ((uint)id < 256)      b3 |= 1UL << (id - 192);
            else if ((uint)id < 320)      b4 |= 1UL << (id - 256);
            else if ((uint)id < 384)      b5 |= 1UL << (id - 320);
            else if ((uint)id < 448)      b6 |= 1UL << (id - 384);
            else if ((uint)id < 512)      b7 |= 1UL << (id - 448);
        }

        return new ComponentMask(b0, b1, b2, b3, b4, b5, b6, b7);
    }
}
