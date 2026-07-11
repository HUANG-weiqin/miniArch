// Frame-derived lookup layout implementations for FrameReadModel ValueLab.
//
// A  — EntityArrayLookup<TKey>    : key → contiguous Entity entries (baseline)
// B  — LinkedRowLookup<TKey>      : open-addressing hash + linked row entries
// C  — CompactRowLookup<TKey>     : two-pass CSR count/prefix/stable scatter
// DI — DenseIntCompactLookup      : bounded int dense count/prefix/stable scatter
//
// All use generation stamps for O(1) Clear. TryBuildNoGrow returns false
// (with empty state) on insufficient capacity. BuildAutoGrow grows as needed
// (while loop with max-attempt safety).

using System.Runtime.CompilerServices;
using MiniArch;

namespace FrameReadModels.ValueLab;

// ========================================================================
//  Shared helpers
// ========================================================================

internal static class LayoutHelpers
{
    /// <summary>Rounds up to the nearest power of two.</summary>
    internal static int CeilPow2(int n)
    {
        if (n <= 0) return 1;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }
}

// ========================================================================
//  Layout A: EntityArrayLookup<TKey> — key → contiguous entity slice
//  Baseline. Single-pass build. Stores Entity values per key in slabs.
// ========================================================================

internal struct EntityArrayLookup<TKey> : IFrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    // --- Hash table ---
    private TKey[] _keys;
    private int[] _stamps;      // generation stamp per slot; 0 = empty
    private int _generation;

    // --- Entity storage per key ---
    private Entity[] _entitySlab;
    private int[] _keyStart;    // start index in _entitySlab for each key slot
    private int[] _keyCount;    // entity count per key slot
    private int _slabCursor;

    private int _capacity;      // hash table capacity (distinct keys)
    private int _entityCapacity;
    private int _keyCountTotal;

    private BuildResult _lastResult;

    // Recycled scratch array for temp counts (avoids steady-state allocation).
    private int[]? _scratchCounts;

    private EntityArrayLookup(int keyCapacity, int entityCapacity) : this()
    {
        _capacity = LayoutHelpers.CeilPow2(keyCapacity > 0 ? keyCapacity : 16);
        _entityCapacity = LayoutHelpers.CeilPow2(entityCapacity > 0 ? entityCapacity : 64);
        _keys = new TKey[_capacity];
        _stamps = new int[_capacity];
        _keyStart = new int[_capacity];
        _keyCount = new int[_capacity];
        _entitySlab = new Entity[_entityCapacity];
        _generation = 1;
    }

    public static EntityArrayLookup<TKey> Create(int keyCapacity = 16, int entityCapacity = 64)
        => new(keyCapacity, entityCapacity);

    public void Clear()
    {
        _generation++;
        _slabCursor = 0;
        _keyCountTotal = 0;

        _lastResult = default;
    }

    public BuildResult LastResult => _lastResult;
    public int KeyCount => _keyCountTotal;
    public int TotalRows => _slabCursor;

    public int GetRowCount(TKey key)
    {
        var idx = FindSlot(key);
        return idx >= 0 ? _keyCount[idx] : 0;
    }

    public int CopyEntities(TKey key, Span<Entity> dest, ReadOnlySpan<ChunkView> chunks)
    {
        var idx = FindSlot(key);
        if (idx < 0) return 0;
        var start = _keyStart[idx];
        var count = _keyCount[idx];
        if (count > dest.Length) count = dest.Length;
        new ReadOnlySpan<Entity>(_entitySlab, start, count).CopyTo(dest);
        return count;
    }

    public int CopyRowRefs(TKey key, Span<RowRef> dest)
    {
        // EntityArrayLookup stores entities, not RowRefs.
        // Synthesizing would require full chunk scan — not efficiently supported.
        // Callers should use CopyEntities for consumption.
        return 0;
    }

    // ---- Scratch array helper ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int[] GetScratchCounts()
    {
        if (_scratchCounts == null || _scratchCounts.Length < _capacity)
        {
            _scratchCounts = new int[LayoutHelpers.CeilPow2(_capacity)];
        }
        else
        {
            Array.Clear(_scratchCounts, 0, _capacity);
        }
        return _scratchCounts;
    }

    // ---- Build (1-arity) ----

    public bool TryBuildNoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        Clear();
        var p = pred;
        var s = sel;
        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;
        var maxBucket = 0;

        // Phase 1: count distinct keys and total matches
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri]);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys, ref maxBucket);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
                if (tempCounts[slot] > maxBucket) maxBucket = tempCounts[slot];
            }
        }

        if (distinctKeys > _capacity || totalMatches > _entityCapacity)
        {
            Clear();
            return false;
        }

        // Phase 2: compute prefix and store entities
        var cursor = 0;
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation) continue;
            _keyStart[i] = cursor;
            _keyCount[i] = tempCounts[i];
            cursor += tempCounts[i];
        }

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri]);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _entitySlab[pos] = entities[ri];
            }
        }

        _slabCursor = cursor;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, cursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 256);
            var newEntityCap = Math.Max(_entityCapacity * 2, 4096);
            var saved = _scratchCounts;
            this = Create(newKeyCap, newEntityCap);
            _scratchCounts = saved;
        }
        throw new InvalidOperationException(
            "EntityArrayLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (2-arity) ----

    public bool TryBuildNoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        Clear();
        var p = pred;
        var s = sel;
        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;
        var maxBucket = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys, ref maxBucket);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
                if (tempCounts[slot] > maxBucket) maxBucket = tempCounts[slot];
            }
        }

        if (distinctKeys > _capacity || totalMatches > _entityCapacity)
        {
            Clear();
            return false;
        }

        var cursor = 0;
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation) continue;
            _keyStart[i] = cursor;
            _keyCount[i] = tempCounts[i];
            cursor += tempCounts[i];
        }

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _entitySlab[pos] = entities[ri];
            }
        }

        _slabCursor = cursor;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, cursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 256);
            var newEntityCap = Math.Max(_entityCapacity * 2, 4096);
            var saved = _scratchCounts;
            this = Create(newKeyCap, newEntityCap);
            _scratchCounts = saved;
        }
        throw new InvalidOperationException(
            "EntityArrayLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (3-arity) ----

    public bool TryBuildNoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        Clear();
        var p = pred;
        var s = sel;
        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;
        var maxBucket = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys, ref maxBucket);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
                if (tempCounts[slot] > maxBucket) maxBucket = tempCounts[slot];
            }
        }

        if (distinctKeys > _capacity || totalMatches > _entityCapacity)
        {
            Clear();
            return false;
        }

        var cursor = 0;
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation) continue;
            _keyStart[i] = cursor;
            _keyCount[i] = tempCounts[i];
            cursor += tempCounts[i];
        }

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _entitySlab[pos] = entities[ri];
            }
        }

        _slabCursor = cursor;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, cursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, T3, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 256);
            var newEntityCap = Math.Max(_entityCapacity * 2, 4096);
            var saved = _scratchCounts;
            this = Create(newKeyCap, newEntityCap);
            _scratchCounts = saved;
        }
        throw new InvalidOperationException(
            "EntityArrayLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Private helpers ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSlot(TKey key)
    {
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        while (true)
        {
            if (_stamps[idx] != _generation) return -1;
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key)) return (int)idx;
            idx = (idx + 1) & mask;
        }
    }

    private int FindOrCreateSlot(TKey key, int[] tempCounts, ref int distinctKeys, ref int maxBucket)
    {
        if (distinctKeys >= _capacity) return -1;
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        while (true)
        {
            if (_stamps[idx] != _generation)
            {
                _stamps[idx] = _generation;
                _keys[idx] = key;
                distinctKeys++;
                return (int)idx;
            }
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key))
                return (int)idx;
            idx = (idx + 1) & mask;
        }
    }
}

// ========================================================================
//  Layout B: LinkedRowLookup<TKey> — open-addressing hash + linked row entries
//  Single-pass build. Tail-append preserves scan order.
//  No temp-count scratch array needed (single-pass, no counting phase).
// ========================================================================

internal struct LinkedRowLookup<TKey> : IFrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    // --- Hash table ---
    private TKey[] _keys;
    private int[] _stamps;
    private int _generation;
    private int[] _headIdx;     // index into _rowEntries for head of chain (-1 = none)
    private int[] _tailIdx;     // index into _rowEntries for tail of chain (-1 = none)
    private int[] _rowCounts;   // per-slot row count

    // --- Row entries (linked) ---
    private RowRef[] _rowRefs;
    private int[] _nextIndices; // next index in chain (-1 = end)
    private int _rowCursor;     // next free index in _rowRefs

    private int _capacity;
    private int _rowCapacity;
    private int _keyCountTotal;

    private BuildResult _lastResult;

    private LinkedRowLookup(int keyCapacity, int rowCapacity) : this()
    {
        _capacity = LayoutHelpers.CeilPow2(Math.Max(keyCapacity, 16));
        _rowCapacity = LayoutHelpers.CeilPow2(Math.Max(rowCapacity, 64));
        _keys = new TKey[_capacity];
        _stamps = new int[_capacity];
        _headIdx = new int[_capacity];
        _tailIdx = new int[_capacity];
        _rowCounts = new int[_capacity];
        _rowRefs = new RowRef[_rowCapacity];
        _nextIndices = new int[_rowCapacity];
        _generation = 1;
    }

    public static LinkedRowLookup<TKey> Create(int keyCapacity = 16, int rowCapacity = 64)
        => new(keyCapacity, rowCapacity);

    public void Clear()
    {
        _generation++;
        _rowCursor = 0;
        _keyCountTotal = 0;

        _lastResult = default;
    }

    public BuildResult LastResult => _lastResult;
    public int KeyCount => _keyCountTotal;
    public int TotalRows => _rowCursor;

    public int GetRowCount(TKey key)
    {
        var idx = FindSlot(key);
        return idx >= 0 ? _rowCounts[idx] : 0;
    }

    public int CopyEntities(TKey key, Span<Entity> dest, ReadOnlySpan<ChunkView> chunks)
    {
        var idx = FindSlot(key);
        if (idx < 0) return 0;
        var ri = _headIdx[idx];
        var written = 0;
        while (ri >= 0 && written < dest.Length)
        {
            ref readonly var rr = ref _rowRefs[ri];
            dest[written] = chunks[rr.ChunkIndex].GetEntities()[rr.RowIndex];
            written++;
            ri = _nextIndices[ri];
        }
        return written;
    }

    public int CopyRowRefs(TKey key, Span<RowRef> dest)
    {
        var idx = FindSlot(key);
        if (idx < 0) return 0;
        var ri = _headIdx[idx];
        var written = 0;
        while (ri >= 0 && written < dest.Length)
        {
            dest[written] = _rowRefs[ri];
            written++;
            ri = _nextIndices[ri];
        }
        return written;
    }

    // ---- Build (1-arity) ----

    public bool TryBuildNoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        Clear();
        var p = pred;
        var s = sel;
        var distinctKeys = 0;
        var maxBucket = 0;
        var totalMatches = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                totalMatches++;

                if (totalMatches > _rowCapacity)
                {
                    Clear();
                    return false;
                }

                var key = s.Select(entities[ri], in c1[ri]);
                var slot = FindOrCreateSlot(key, ref distinctKeys);
                if (slot < 0)
                {
                    Clear();
                    return false;
                }

                var rowIdx = _rowCursor++;
                _rowRefs[rowIdx] = new RowRef(ci, ri);

                if (_headIdx[slot] == -1)
                {
                    _headIdx[slot] = rowIdx;
                    _tailIdx[slot] = rowIdx;
                    _nextIndices[rowIdx] = -1;
                }
                else
                {
                    _nextIndices[_tailIdx[slot]] = rowIdx;
                    _tailIdx[slot] = rowIdx;
                    _nextIndices[rowIdx] = -1;
                }

                _rowCounts[slot]++;
                if (_rowCounts[slot] > maxBucket) maxBucket = _rowCounts[slot];
            }
        }

        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, _rowCursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 64);
            var newRowCap = Math.Max(_rowCapacity * 2, 4096);
            this = Create(newKeyCap, newRowCap);
        }
        throw new InvalidOperationException(
            "LinkedRowLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (2-arity) ----

    public bool TryBuildNoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        Clear();
        var p = pred;
        var s = sel;
        var distinctKeys = 0;
        var maxBucket = 0;
        var totalMatches = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                totalMatches++;

                if (totalMatches > _rowCapacity)
                {
                    Clear();
                    return false;
                }

                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                var slot = FindOrCreateSlot(key, ref distinctKeys);
                if (slot < 0)
                {
                    Clear();
                    return false;
                }

                var rowIdx = _rowCursor++;
                _rowRefs[rowIdx] = new RowRef(ci, ri);

                if (_headIdx[slot] == -1)
                {
                    _headIdx[slot] = rowIdx;
                    _tailIdx[slot] = rowIdx;
                    _nextIndices[rowIdx] = -1;
                }
                else
                {
                    _nextIndices[_tailIdx[slot]] = rowIdx;
                    _tailIdx[slot] = rowIdx;
                    _nextIndices[rowIdx] = -1;
                }

                _rowCounts[slot]++;
                if (_rowCounts[slot] > maxBucket) maxBucket = _rowCounts[slot];
            }
        }

        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, _rowCursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 64);
            var newRowCap = Math.Max(_rowCapacity * 2, 4096);
            this = Create(newKeyCap, newRowCap);
        }
        throw new InvalidOperationException(
            "LinkedRowLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (3-arity) ----

    public bool TryBuildNoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        Clear();
        var p = pred;
        var s = sel;
        var distinctKeys = 0;
        var maxBucket = 0;
        var totalMatches = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                totalMatches++;

                if (totalMatches > _rowCapacity)
                {
                    Clear();
                    return false;
                }

                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                var slot = FindOrCreateSlot(key, ref distinctKeys);
                if (slot < 0)
                {
                    Clear();
                    return false;
                }

                var rowIdx = _rowCursor++;
                _rowRefs[rowIdx] = new RowRef(ci, ri);

                if (_headIdx[slot] == -1)
                {
                    _headIdx[slot] = rowIdx;
                    _tailIdx[slot] = rowIdx;
                    _nextIndices[rowIdx] = -1;
                }
                else
                {
                    _nextIndices[_tailIdx[slot]] = rowIdx;
                    _tailIdx[slot] = rowIdx;
                    _nextIndices[rowIdx] = -1;
                }

                _rowCounts[slot]++;
                if (_rowCounts[slot] > maxBucket) maxBucket = _rowCounts[slot];
            }
        }

        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, _rowCursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, T3, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 64);
            var newRowCap = Math.Max(_rowCapacity * 2, 4096);
            this = Create(newKeyCap, newRowCap);
        }
        throw new InvalidOperationException(
            "LinkedRowLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Private helpers ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSlot(TKey key)
    {
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        while (true)
        {
            if (_stamps[idx] != _generation) return -1;
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key)) return (int)idx;
            idx = (idx + 1) & mask;
        }
    }

    private int FindOrCreateSlot(TKey key, ref int distinctKeys)
    {
        if (distinctKeys >= _capacity) return -1;
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        while (true)
        {
            if (_stamps[idx] != _generation)
            {
                _stamps[idx] = _generation;
                _keys[idx] = key;
                _headIdx[idx] = -1;
                _tailIdx[idx] = -1;
                _rowCounts[idx] = 0;
                distinctKeys++;
                return (int)idx;
            }
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key))
                return (int)idx;
            idx = (idx + 1) & mask;
        }
    }
}

// ========================================================================
//  Layout C: CompactRowLookup<TKey> — two-pass CSR
//  Pass 1: count per key. Pass 2: prefix sum, stable scatter.
//  Row refs contiguous per key for optimal read locality.
// ========================================================================

internal struct CompactRowLookup<TKey> : IFrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    // --- Hash table ---
    private TKey[] _keys;
    private int[] _stamps;
    private int _generation;
    private int[] _keyStart;   // start index in _flatRows for each key slot
    private int[] _keyCount;   // row count per key slot

    // --- Flat row storage (CSR) ---
    private RowRef[] _flatRows;
    private int _totalRows;

    private int _capacity;
    private int _rowCapacity;
    private int _keyCountTotal;

    private BuildResult _lastResult;

    // Recycled scratch array for temp counts.
    private int[]? _scratchCounts;

    private CompactRowLookup(int keyCapacity, int rowCapacity) : this()
    {
        _capacity = LayoutHelpers.CeilPow2(Math.Max(keyCapacity, 16));
        _rowCapacity = LayoutHelpers.CeilPow2(Math.Max(rowCapacity, 64));
        _keys = new TKey[_capacity];
        _stamps = new int[_capacity];
        _keyStart = new int[_capacity];
        _keyCount = new int[_capacity];
        _flatRows = new RowRef[_rowCapacity];
        _generation = 1;
    }

    public static CompactRowLookup<TKey> Create(int keyCapacity = 16, int rowCapacity = 64)
        => new(keyCapacity, rowCapacity);

    public void Clear()
    {
        _generation++;
        _totalRows = 0;
        _keyCountTotal = 0;

        _lastResult = default;
    }

    public BuildResult LastResult => _lastResult;
    public int KeyCount => _keyCountTotal;
    public int TotalRows => _totalRows;

    public int GetRowCount(TKey key)
    {
        var idx = FindSlot(key);
        return idx >= 0 ? _keyCount[idx] : 0;
    }

    public int CopyEntities(TKey key, Span<Entity> dest, ReadOnlySpan<ChunkView> chunks)
    {
        var idx = FindSlot(key);
        if (idx < 0) return 0;
        var start = _keyStart[idx];
        var count = _keyCount[idx];
        if (count > dest.Length) count = dest.Length;
        for (var i = 0; i < count; i++)
        {
            ref readonly var rr = ref _flatRows[start + i];
            dest[i] = chunks[rr.ChunkIndex].GetEntities()[rr.RowIndex];
        }
        return count;
    }

    public int CopyRowRefs(TKey key, Span<RowRef> dest)
    {
        var idx = FindSlot(key);
        if (idx < 0) return 0;
        var start = _keyStart[idx];
        var count = _keyCount[idx];
        if (count > dest.Length) count = dest.Length;
        new ReadOnlySpan<RowRef>(_flatRows, start, count).CopyTo(dest);
        return count;
    }

    // ---- Scratch array helper ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int[] GetScratchCounts()
    {
        if (_scratchCounts == null || _scratchCounts.Length < _capacity)
        {
            _scratchCounts = new int[LayoutHelpers.CeilPow2(_capacity)];
        }
        else
        {
            Array.Clear(_scratchCounts, 0, _capacity);
        }
        return _scratchCounts;
    }

    // ---- Build (1-arity) ----

    public bool TryBuildNoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        Clear();
        var p = pred;
        var s = sel;
        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;
        var maxBucket = 0;

        // Pass 1: count per key
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri]);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys, ref maxBucket);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
                if (tempCounts[slot] > maxBucket) maxBucket = tempCounts[slot];
            }
        }

        if (distinctKeys > _capacity || totalMatches > _rowCapacity)
        {
            Clear();
            return false;
        }

        // Compute prefix
        var cursor = 0;
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation) continue;
            _keyStart[i] = cursor;
            _keyCount[i] = tempCounts[i];
            cursor += tempCounts[i];
        }

        // Pass 2: stable scatter
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri]);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _flatRows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = cursor;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, cursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 64);
            var newRowCap = Math.Max(_rowCapacity * 2, 4096);
            var saved = _scratchCounts;
            this = Create(newKeyCap, newRowCap);
            _scratchCounts = saved;
        }
        throw new InvalidOperationException(
            "CompactRowLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (2-arity) ----

    public bool TryBuildNoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        Clear();
        var p = pred;
        var s = sel;
        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;
        var maxBucket = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys, ref maxBucket);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
                if (tempCounts[slot] > maxBucket) maxBucket = tempCounts[slot];
            }
        }

        if (distinctKeys > _capacity || totalMatches > _rowCapacity)
        {
            Clear();
            return false;
        }

        var cursor = 0;
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation) continue;
            _keyStart[i] = cursor;
            _keyCount[i] = tempCounts[i];
            cursor += tempCounts[i];
        }

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _flatRows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = cursor;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, cursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 64);
            var newRowCap = Math.Max(_rowCapacity * 2, 4096);
            var saved = _scratchCounts;
            this = Create(newKeyCap, newRowCap);
            _scratchCounts = saved;
        }
        throw new InvalidOperationException(
            "CompactRowLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (3-arity) ----

    public bool TryBuildNoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        Clear();
        var p = pred;
        var s = sel;
        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;
        var maxBucket = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys, ref maxBucket);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
                if (tempCounts[slot] > maxBucket) maxBucket = tempCounts[slot];
            }
        }

        if (distinctKeys > _capacity || totalMatches > _rowCapacity)
        {
            Clear();
            return false;
        }

        var cursor = 0;
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation) continue;
            _keyStart[i] = cursor;
            _keyCount[i] = tempCounts[i];
            cursor += tempCounts[i];
        }

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _flatRows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = cursor;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, cursor, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, T3, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newKeyCap = Math.Max(_capacity * 2, 64);
            var newRowCap = Math.Max(_rowCapacity * 2, 4096);
            var saved = _scratchCounts;
            this = Create(newKeyCap, newRowCap);
            _scratchCounts = saved;
        }
        throw new InvalidOperationException(
            "CompactRowLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Private helpers ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSlot(TKey key)
    {
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        while (true)
        {
            if (_stamps[idx] != _generation) return -1;
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key)) return (int)idx;
            idx = (idx + 1) & mask;
        }
    }

    private int FindOrCreateSlot(TKey key, int[] tempCounts, ref int distinctKeys, ref int maxBucket)
    {
        if (distinctKeys >= _capacity) return -1;
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        while (true)
        {
            if (_stamps[idx] != _generation)
            {
                _stamps[idx] = _generation;
                _keys[idx] = key;
                distinctKeys++;
                return (int)idx;
            }
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key))
                return (int)idx;
            idx = (idx + 1) & mask;
        }
    }
}

// ========================================================================
//  Layout DI: DenseIntCompactLookup — bounded int dense CSR
//  For integer keys in a known range [0, maxKeyValue].
//  No hashing — dense counts array directly indexed by key.
// ========================================================================

internal struct DenseIntCompactLookup : IFrameLookup<int>
{
    private int _maxKeyValue;
    private int[] _counts;
    private int[] _prefix;     // prefix sum (start index per key, length maxKeyValue+2)
    private RowRef[] _rows;
    private int _totalRows;
    private int _keyCountTotal;
    private bool _initialized;

    private BuildResult _lastResult;

    // Recycled scratch array for scatter positions (avoid new[] on every build).
    private int[]? _scratchPos;

    private DenseIntCompactLookup(int maxKeyValue, int rowCapacity) : this()
    {
        _maxKeyValue = maxKeyValue;
        var size = maxKeyValue + 1;
        _counts = new int[size];
        _prefix = new int[size + 1];
        _rows = new RowRef[rowCapacity];
        _initialized = true;
    }

    public static DenseIntCompactLookup Create(int maxKeyValue, int rowCapacity = 64)
        => new(maxKeyValue, rowCapacity);

    public void Clear()
    {
        if (!_initialized) return;
        Array.Clear(_counts, 0, _counts.Length);
        Array.Clear(_prefix, 0, _prefix.Length);
        _totalRows = 0;
        _keyCountTotal = 0;

        _lastResult = default;
    }

    public BuildResult LastResult => _lastResult;
    public int KeyCount => _keyCountTotal;
    public int TotalRows => _totalRows;

    public int GetRowCount(int key)
    {
        if (!_initialized || (uint)key > (uint)_maxKeyValue) return 0;
        return _counts[key];
    }

    public int CopyEntities(int key, Span<Entity> dest, ReadOnlySpan<ChunkView> chunks)
    {
        if (!_initialized || (uint)key > (uint)_maxKeyValue) return 0;
        var start = _prefix[key];
        var count = _counts[key];
        if (count > dest.Length) count = dest.Length;
        for (var i = 0; i < count; i++)
        {
            ref readonly var rr = ref _rows[start + i];
            dest[i] = chunks[rr.ChunkIndex].GetEntities()[rr.RowIndex];
        }
        return count;
    }

    public int CopyRowRefs(int key, Span<RowRef> dest)
    {
        if (!_initialized || (uint)key > (uint)_maxKeyValue) return 0;
        var start = _prefix[key];
        var count = _counts[key];
        if (count > dest.Length) count = dest.Length;
        new ReadOnlySpan<RowRef>(_rows, start, count).CopyTo(dest);
        return count;
    }

    // ---- Scratch array helper ----

    private int[] GetScratchPos()
    {
        var size = _maxKeyValue + 1;
        if (_scratchPos == null || _scratchPos.Length < size)
        {
            _scratchPos = new int[LayoutHelpers.CeilPow2(size)];
        }
        // Not cleared — every entry will be overwritten from _prefix before use.
        return _scratchPos;
    }

    // ---- Build (1-arity) ----

    public bool TryBuildNoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<int, T1>
    {
        Clear();
        if (!_initialized) return false;
        var p = pred;
        var s = sel;
        var totalMatches = 0;
        var maxBucket = 0;
        var distinctKeys = 0;

        // Pass 1: count
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri]);
                if ((uint)key > (uint)_maxKeyValue)
                {
                    Clear();
                    return false;
                }
                if (_counts[key] == 0) distinctKeys++;
                _counts[key]++;
                if (_counts[key] > maxBucket) maxBucket = _counts[key];
            }
        }

        if (totalMatches > _rows.Length)
        {
            Clear();
            return false;
        }

        // Prefix sum
        for (var i = 0; i <= _maxKeyValue; i++)
            _prefix[i + 1] = _prefix[i] + _counts[i];

        // Pass 2: stable scatter via scratch array
        var scatterPos = GetScratchPos();
        for (var i = 0; i <= _maxKeyValue; i++)
            scatterPos[i] = _prefix[i];

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri]);
                var pos = scatterPos[key]++;
                _rows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = totalMatches;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, totalMatches, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<int, T1>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            // Grow both row capacity and key range.
            var newRowCap = Math.Max(_rows.Length * 2, 4096);
            var newMaxKeyValue = Math.Max(_maxKeyValue * 2 + 1, 1023);
            var saved = _scratchPos;
            this = Create(newMaxKeyValue, newRowCap);
            _scratchPos = saved;
        }
        throw new InvalidOperationException(
            "DenseIntCompactLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (2-arity) ----

    public bool TryBuildNoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<int, T1, T2>
    {
        Clear();
        if (!_initialized) return false;
        var p = pred;
        var s = sel;
        var totalMatches = 0;
        var maxBucket = 0;
        var distinctKeys = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                if ((uint)key > (uint)_maxKeyValue)
                {
                    Clear();
                    return false;
                }
                if (_counts[key] == 0) distinctKeys++;
                _counts[key]++;
                if (_counts[key] > maxBucket) maxBucket = _counts[key];
            }
        }

        if (totalMatches > _rows.Length)
        {
            Clear();
            return false;
        }

        for (var i = 0; i <= _maxKeyValue; i++)
            _prefix[i + 1] = _prefix[i] + _counts[i];

        var scatterPos = GetScratchPos();
        for (var i = 0; i <= _maxKeyValue; i++)
            scatterPos[i] = _prefix[i];

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri]);
                var pos = scatterPos[key]++;
                _rows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = totalMatches;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, totalMatches, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<int, T1, T2>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newRowCap = Math.Max(_rows.Length * 2, 4096);
            var newMaxKeyValue = Math.Max(_maxKeyValue * 2 + 1, 1023);
            var saved = _scratchPos;
            this = Create(newMaxKeyValue, newRowCap);
            _scratchPos = saved;
        }
        throw new InvalidOperationException(
            "DenseIntCompactLookup.BuildAutoGrow failed after maximum attempts.");
    }

    // ---- Build (3-arity) ----

    public bool TryBuildNoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<int, T1, T2, T3>
    {
        Clear();
        if (!_initialized) return false;
        var p = pred;
        var s = sel;
        var totalMatches = 0;
        var maxBucket = 0;
        var distinctKeys = 0;

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                totalMatches++;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                if ((uint)key > (uint)_maxKeyValue)
                {
                    Clear();
                    return false;
                }
                if (_counts[key] == 0) distinctKeys++;
                _counts[key]++;
                if (_counts[key] > maxBucket) maxBucket = _counts[key];
            }
        }

        if (totalMatches > _rows.Length)
        {
            Clear();
            return false;
        }

        for (var i = 0; i <= _maxKeyValue; i++)
            _prefix[i + 1] = _prefix[i] + _counts[i];

        var scatterPos = GetScratchPos();
        for (var i = 0; i <= _maxKeyValue; i++)
            scatterPos[i] = _prefix[i];

        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            var c1 = chunk.GetSpan<T1>();
            var c2 = chunk.GetSpan<T2>();
            var c3 = chunk.GetSpan<T3>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                if (!p.Match(entities[ri], in c1[ri], in c2[ri], in c3[ri])) continue;
                var key = s.Select(entities[ri], in c1[ri], in c2[ri], in c3[ri]);
                var pos = scatterPos[key]++;
                _rows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = totalMatches;
        _keyCountTotal = distinctKeys;

        _lastResult = new BuildResult(totalMatches, totalMatches, distinctKeys, maxBucket, false);
        return true;
    }

    public void BuildAutoGrow<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<int, T1, T2, T3>
    {
        const int MaxAttempts = 16;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (TryBuildNoGrow<T1, T2, T3, TPred, TSel>(chunks, ref pred, ref sel))
            {
                if (attempt > 0)
                    _lastResult = new BuildResult(_lastResult.MatchedRows, _lastResult.StoredRows,
                        _lastResult.DistinctKeys, _lastResult.MaxBucketSize, resized: true);
                return;
            }
            var newRowCap = Math.Max(_rows.Length * 2, 4096);
            var newMaxKeyValue = Math.Max(_maxKeyValue * 2 + 1, 1023);
            var saved = _scratchPos;
            this = Create(newMaxKeyValue, newRowCap);
            _scratchPos = saved;
        }
        throw new InvalidOperationException(
            "DenseIntCompactLookup.BuildAutoGrow failed after maximum attempts.");
    }
}
