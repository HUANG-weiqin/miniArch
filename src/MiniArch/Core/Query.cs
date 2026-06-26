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

    internal QueryFilter Filter => _filter;

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
                    RefreshViewsOnly();
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefreshViewsOnly()
    {
        lock (_refreshLock)
        {
            RebuildChunkViews();
            Interlocked.Increment(ref _refreshCount);
        }
    }

    /// <summary>
    /// Rebuilds chunk views for already-matched archetypes when their segment
    /// count changes (chunked growth) without any new archetypes being added.
    /// </summary>
    private void RebuildChunkViews()
    {
        var totalViews = 0;
        for (var i = 0; i < _matchedArchetypeCount; i++)
        {
            var a = _snapshotArchetypes[i];
            totalViews += a.IsChunked ? a.SegmentCount : 1;
        }

        if (_snapshotChunkViews.Length < totalViews)
        {
            var newLen = Math.Max(totalViews, _snapshotChunkViews.Length == 0 ? 4 : _snapshotChunkViews.Length * 2);
            Array.Resize(ref _snapshotChunkViews, newLen);
        }

        var ci = 0;
        for (var i = 0; i < _matchedArchetypeCount; i++)
        {
            var a = _snapshotArchetypes[i];
            var viewCount = a.IsChunked ? a.SegmentCount : 1;
            _archetypeExpectedViews[i] = viewCount;

            if (a.IsChunked)
            {
                for (var s = 0; s < a.SegmentCount; s++)
                    _snapshotChunkViews[ci++] = new ChunkView(a, s);
            }
            else
            {
                _snapshotChunkViews[ci++] = new ChunkView(a);
            }
        }

        Volatile.Write(ref _chunkViewCount, ci);
    }

    private void AppendNewArchetypes(int currentArchetypeCount)
    {
        var archetypes = _world.Archetypes;
        var start = _lastArchetypeCount < 0 ? 0 : _lastArchetypeCount;

        // ── Incremental append: only scan new archetypes ──
        // Existing matched archetypes are already in the snapshot; we only
        // need to test archetypes added since the last refresh.
        var newMatchCount = 0;
        for (var i = start; i < currentArchetypeCount; i++)
        {
            if (Matches(archetypes[i]))
                newMatchCount++;
        }

        var totalUnique = _matchedArchetypeCount + newMatchCount;

        // Grow arrays if needed.
        if (_snapshotArchetypes.Length < totalUnique)
        {
            var newLen = Math.Max(totalUnique, _snapshotArchetypes.Length == 0 ? 4 : _snapshotArchetypes.Length * 2);
            Array.Resize(ref _snapshotArchetypes, newLen);
        }
        if (_archetypeExpectedViews.Length < totalUnique)
            Array.Resize(ref _archetypeExpectedViews, Math.Max(totalUnique, _archetypeExpectedViews.Length == 0 ? 4 : _archetypeExpectedViews.Length * 2));

        // Count total views (existing + new).
        var totalViews = _chunkViewCount;
        for (var i = start; i < currentArchetypeCount; i++)
        {
            var a = archetypes[i];
            if (!Matches(a)) continue;
            totalViews += a.IsChunked ? a.SegmentCount : 1;
        }

        if (_snapshotChunkViews.Length < totalViews)
        {
            var newLen = Math.Max(totalViews, _snapshotChunkViews.Length == 0 ? 4 : _snapshotChunkViews.Length * 2);
            Array.Resize(ref _snapshotChunkViews, newLen);
        }

        // Append new matching archetypes + their chunk views.
        var ai = _matchedArchetypeCount;
        var ci = _chunkViewCount;
        for (var i = start; i < currentArchetypeCount; i++)
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
        if (!_requiredMask.IsZero() && !archMask.IsSupersetOf(_requiredMask))
            return false;

        // Excluded: archetype must have none of the excluded components.
        if (!_excludedMask.IsZero() && archMask.Intersects(_excludedMask))
            return false;

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
        if (!_anyMask.IsZero() && archMask.Intersects(_anyMask))
            return true;

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
        var builder = new MaskBuilder();
        for (var i = 0; i < components.Length; i++)
        {
            builder.SetBit(components[i].Value);
        }
        return builder.ToMask();
    }
}
