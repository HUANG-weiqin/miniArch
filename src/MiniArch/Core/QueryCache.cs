using System.Runtime.CompilerServices;

namespace MiniArch.Core;

using MiniArch;

/// <summary>
/// Cached archetype query with sorted-by-signature snapshot invalidation.
/// Archetypes are never removed; the world's _archetypeSnapshot is always
/// sorted by Signature.ComponentType.Value. Matched archetypes are rebuilt
/// from scratch on any count change (cold path).
/// </summary>
internal sealed class QueryCache
{
    private readonly World _world;
    private readonly QueryFilter _filter;
    private readonly object _refreshLock = new();
    private int _refreshCount;

    // Pre-computed filter masks (immutable after construction).
    private readonly ComponentMask _requiredMask;
    private readonly ComponentMask _excludedMask;
    private readonly ComponentMask _anyMask;
    private readonly bool _exact;

    // Unique matched archetypes (no duplicates, for entity enumeration).
    private Archetype[] _snapshotArchetypes = [];
    private int _matchedArchetypeCount;

    // One ChunkView per segment (for chunk iteration).
    private ChunkView[] _snapshotChunkViews = [];
    private int _chunkViewCount;

    private int _lastArchetypeCount = -1;

    // Tracks expected view shape per archetype (indexed parallel to _snapshotArchetypes).
    // Non-chunked = NonChunkedShape (-1), chunked = SegmentCount.
    // Even when both have 1 view, the ChunkView segment index differs (-1 vs 0).
    private int[] _archetypeExpectedViews = [];

    internal QueryCache(World world, QueryFilter filter)
    {
        _world = world;
        _filter = filter;
        _requiredMask = ComputeFilterMask(filter.Required.AsSpan());
        _excludedMask = ComputeFilterMask(filter.Excluded.AsSpan());
        _anyMask = ComputeFilterMask(filter.Any.AsSpan());
        _exact = filter.Exact;
    }

    internal static QueryCache Create(World world, in QueryDescription description)
    {
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
        _world.AssertNoStructChange();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureRefreshed()
    {
        var currentCount = _world.ArchetypeCount;
        if (currentCount != Volatile.Read(ref _lastArchetypeCount))
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
                var expected = ExpectedViewShape(arch);
                if (expected != Volatile.Read(ref _archetypeExpectedViews[i]))
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
            // Double-check: another thread may have refreshed while we waited.
            if (_world.ArchetypeCount == Volatile.Read(ref _lastArchetypeCount))
                return;
            RebuildCache();
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

        // Publish expected shapes last: readers use these as the fast-path
        // "view snapshot is current" signal. If they still see the old shape,
        // they will take the lock and wait instead of observing stale views.
        for (var i = 0; i < _matchedArchetypeCount; i++)
            Volatile.Write(ref _archetypeExpectedViews[i], ExpectedViewShape(_snapshotArchetypes[i]));
    }

    private void RebuildCache()
    {
        var archetypes = _world.Archetypes; // sorted by signature
        var archCount = archetypes.Length;

        // Single pass: count matches and total views simultaneously.
        var matchCount = 0;
        var totalViews = 0;
        for (var i = 0; i < archCount; i++)
        {
            if (!Matches(archetypes[i])) continue;
            matchCount++;
            totalViews += archetypes[i].IsChunked ? archetypes[i].SegmentCount : 1;
        }

        // Resize arrays.
        if (_snapshotArchetypes.Length < matchCount)
        {
            var newLen = Math.Max(matchCount, _snapshotArchetypes.Length == 0 ? 4 : _snapshotArchetypes.Length * 2);
            Array.Resize(ref _snapshotArchetypes, newLen);
        }
        if (_archetypeExpectedViews.Length < matchCount)
            Array.Resize(ref _archetypeExpectedViews, Math.Max(matchCount, _archetypeExpectedViews.Length == 0 ? 4 : _archetypeExpectedViews.Length * 2));
        if (_snapshotChunkViews.Length < totalViews)
        {
            var newLen = Math.Max(totalViews, _snapshotChunkViews.Length == 0 ? 4 : _snapshotChunkViews.Length * 2);
            Array.Resize(ref _snapshotChunkViews, newLen);
        }

        // Rebuild matched archetypes and chunk views in world order.
        var ai = 0;
        var ci = 0;
        for (var i = 0; i < archCount; i++)
        {
            var a = archetypes[i];
            if (!Matches(a)) continue;

            _snapshotArchetypes[ai] = a;
            _archetypeExpectedViews[ai] = ExpectedViewShape(a);

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

        // Clear trailing slots (previous entries beyond new matchCount).
        for (var i = matchCount; i < _matchedArchetypeCount; i++)
            _snapshotArchetypes[i] = null!;
        for (var i = matchCount; i < _matchedArchetypeCount; i++)
            _archetypeExpectedViews[i] = NonChunkedShape;

        Volatile.Write(ref _chunkViewCount, ci);
        Volatile.Write(ref _matchedArchetypeCount, ai);
        Volatile.Write(ref _lastArchetypeCount, archCount);
    }

    private const int NonChunkedShape = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ExpectedViewShape(Archetype archetype) =>
        archetype.IsChunked ? archetype.SegmentCount : NonChunkedShape;

    /// <summary>Tests whether a single archetype matches this query's filter.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Matches(Archetype archetype)
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

        // Exact: archetype must have exactly the required components (no extras).
        if (_exact && archetype.Signature.Count != required.Length)
            return false;

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

    // ──────────────────────────────────────────────
    //  Fingerprint sort cache (for OrderByEntityId)
    //  Pure-function validation: reads archetype entity storage
    //  directly, no separate scratch buffer needed.
    // ──────────────────────────────────────────────

    // SplitMix64 golden ratio constant.
    private const ulong SplitMix64K = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// Cached result of materialize + sort by <see cref="Entity.Id"/>.
    /// Validated by count pre-check + salted SplitMix64 fingerprint.
    /// Stores ascending order; descending enumeration reverses iteration direction.
    /// </summary>
    private sealed class EntityIdSortCache
    {
        public Entity[] Entities = null!;
        public int Count;
        public ulong Fingerprint;
    }

    private EntityIdSortCache? _entityIdSortCache;
    // Random salt per QueryCache instance prevents deterministic structural collisions.
    // Non-deterministic across runs, but collision probability is 2^-64 per access,
    // which is acceptable for debug UI / serialization use case.
    private readonly ulong _salt = (ulong)Random.Shared.NextInt64() | 1UL;

    /// <summary>
    /// Tries to retrieve the cached sorted entity array.
    /// Validates cache by count pre-check + fingerprint.
    /// On hit: returns cached sorted entities.
    /// On miss: returns false; caller must materialize + sort + call SetEntityIdSortCache.
    /// </summary>
    internal bool TryGetEntityIdSortCache(
        Archetype[] archetypes, int archCount, int currentCount,
        out Entity[] entities, out int count)
    {
        entities = null!;
        count = 0;

        var cache = _entityIdSortCache;
        if (cache is null)
            return false;

        // ── Count pre-check (fast-fail for structural changes) ──
        if (currentCount != cache.Count)
            return false;

        // ── Fingerprint validation (reads directly from archetypes) ──
        if (ComputeFingerprint(archetypes, archCount) != cache.Fingerprint)
            return false;

        // HIT
        entities = cache.Entities;
        count = currentCount;
        return true;
    }

    /// <summary>
    /// Replaces the sort cache with a new sorted array and its fingerprint.
    /// Caller provides a sorted ascending array; ownership transfers to the cache.
    /// Must NOT be a pool-rented array.
    /// </summary>
    internal void SetEntityIdSortCache(
        Archetype[] archetypes, int archCount, int currentCount,
        Entity[] sortedEntities)
    {
        _entityIdSortCache = new EntityIdSortCache
        {
            Entities = sortedEntities,
            Count = currentCount,
            Fingerprint = ComputeFingerprint(archetypes, archCount)
        };
    }

    /// <summary>
    /// Reads all entities from matched archetypes and returns a salted SplitMix64
    /// fingerprint. Zero allocation — reads directly from archetype storage.
    /// Input: matched Archetype[] (already snapshotted by caller).
    /// Output: 64-bit fingerprint. No scratch buffer needed.
    /// </summary>
    private ulong ComputeFingerprint(Archetype[] archetypes, int archCount)
    {
        var salt = _salt;
        ulong fp = 0;

        for (var a = 0; a < archCount; a++)
        {
            var arch = archetypes[a];
            var storage = arch.GetEntityStorageUnsafe();
            var rowCount = arch.EntityCount;

            for (var r = 0; r < rowCount; r++)
            {
                ref readonly var e = ref storage[r];
                var seed = ((ulong)(uint)e.Id | ((ulong)(uint)e.Version << 32)) ^ salt;
                var x = seed + SplitMix64K;
                var z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                fp ^= z ^ (z >> 31);
            }
        }

        return fp;
    }
}
