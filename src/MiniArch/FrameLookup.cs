using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Frame-level derived index that maps a key to a contiguous span of <see cref="RowRef"/>
/// pointing into chunk storage. Build once per frame from a stable World snapshot,
/// then query any number of times.
/// </summary>
public sealed class FrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private const int DefaultKeyCapacity = 16;
    private const int DefaultRowCapacity = 64;
    private const int MaxGrowAttempts = 16;

    // Hash table: key → slot
    private TKey[] _keys;
    private int[] _stamps;
    private int _generation;
    private int[] _keyStart;
    private int[] _keyCount;

    // CSR flat rows
    private RowRef[] _flatRows;
    private int _totalRows;

    private int _capacity;
    private int _rowCapacity;
    private int _keyCountTotal;

    private int[]? _scratchCounts;

    /// <summary>
    /// Creates an empty lookup with the specified initial capacity.
    /// Internal storage is rounded up to the next power of two.
    /// Call <see cref="EnsureCapacity"/> before <see cref="TryBuild{TSel}"/>
    /// if you know the expected key/row counts in advance.
    /// </summary>
    /// <param name="initialKeyCapacity">Hint for the number of distinct keys (0 = default).</param>
    /// <param name="initialRowCapacity">Hint for the total number of rows across all keys (0 = default).</param>
    public FrameLookup(int initialKeyCapacity = 0, int initialRowCapacity = 0)
    {
        _capacity = CeilPow2(Math.Max(initialKeyCapacity, DefaultKeyCapacity));
        _rowCapacity = CeilPow2(Math.Max(initialRowCapacity, DefaultRowCapacity));
        _keys = new TKey[_capacity];
        _stamps = new int[_capacity];
        _keyStart = new int[_capacity];
        _keyCount = new int[_capacity];
        _flatRows = new RowRef[_rowCapacity];
        _generation = 1;
    }

    /// <summary>Number of distinct keys in the current build result.</summary>
    public int KeyCount => _keyCountTotal;

    /// <summary>Total number of stored row references.</summary>
    public int RowCount => _totalRows;

    /// <summary>Resets all stored data. Internal capacity is preserved.</summary>
    public void Clear()
    {
        _generation++;
        _totalRows = 0;
        _keyCountTotal = 0;
    }

    /// <summary>
    /// Ensures the lookup has at least the specified capacity.
    /// Call before <see cref="TryBuild{TSel}"/> if you expect more than the default.
    /// </summary>
    public void EnsureCapacity(int minKeyCapacity, int minRowCapacity)
    {
        if (CeilPow2(minKeyCapacity) > _capacity)
        {
            var newCap = CeilPow2(minKeyCapacity);
            Array.Resize(ref _keys, newCap);
            Array.Resize(ref _stamps, newCap);
            Array.Resize(ref _keyStart, newCap);
            Array.Resize(ref _keyCount, newCap);
            _capacity = newCap;
        }

        if (CeilPow2(minRowCapacity) > _rowCapacity)
        {
            var newRowCap = CeilPow2(minRowCapacity);
            Array.Resize(ref _flatRows, newRowCap);
            _rowCapacity = newRowCap;
        }
    }

    /// <summary>
    /// Returns a zero-copy span of <see cref="RowRef"/> for the given key.
    /// Returns an empty span if the key is not present.
    /// Valid until the next <see cref="Clear"/> or <c>Build</c>.
    /// </summary>
    public ReadOnlySpan<RowRef> this[TKey key]
    {
        get
        {
            var idx = FindSlot(key);
            if (idx < 0) return default;
            return new ReadOnlySpan<RowRef>(_flatRows, _keyStart[idx], _keyCount[idx]);
        }
    }

    // ── Build ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the index from the world snapshot matching the query.
    /// Always succeeds — grows internal storage if needed.
    /// </summary>
    public void Build<TSel>(World world, QueryDescription query, TSel selector = default)
        where TSel : struct, IFrameKeySelector<TKey>
    {
        for (var attempt = 0; attempt < MaxGrowAttempts; attempt++)
        {
            if (TryBuild(world, query, selector))
                return;

            _capacity = Math.Max(_capacity * 2, DefaultKeyCapacity * 2);
            _rowCapacity = Math.Max(_rowCapacity * 2, DefaultRowCapacity * 2);
            var saved = _scratchCounts;
            GrowArrays(_capacity, _rowCapacity);
            _scratchCounts = saved;
        }

        ThrowBuildFailed();
    }

    /// <summary>
    /// Tries to build using the current internal capacity.
    /// Returns false if capacity is exceeded — lookup is cleared on failure.
    /// </summary>
    public bool TryBuild<TSel>(World world, QueryDescription query, TSel selector = default)
        where TSel : struct, IFrameKeySelector<TKey>
    {
        var q = world.Query(query);
        var chunks = q.GetChunks();
        Clear();

        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;

        // Pass 1: count per key
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                totalMatches++;
                var key = selector.Select(entities[ri], chunks, ci, ri);
                var slot = FindOrCreateSlot(key, tempCounts, ref distinctKeys);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
            }
        }

        if (distinctKeys > _capacity || totalMatches > _rowCapacity)
        {
            Clear();
            return false;
        }

        // Prefix sum
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
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                var key = selector.Select(entities[ri], chunks, ci, ri);
                var slot = FindSlot(key);
                var pos = _keyStart[slot] + _keyCount[slot] - tempCounts[slot];
                tempCounts[slot]--;
                _flatRows[pos] = new RowRef(ci, ri);
            }
        }

        _totalRows = cursor;
        _keyCountTotal = distinctKeys;
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────

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

    private int FindOrCreateSlot(TKey key, int[] tempCounts, ref int distinctKeys)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int[] GetScratchCounts()
    {
        if (_scratchCounts == null || _scratchCounts.Length < _capacity)
        {
            _scratchCounts = new int[CeilPow2(_capacity)];
        }
        else
        {
            Array.Clear(_scratchCounts, 0, _capacity);
        }
        return _scratchCounts;
    }

    private void GrowArrays(int newKeyCap, int newRowCap)
    {
        Array.Resize(ref _keys, newKeyCap);
        Array.Resize(ref _stamps, newKeyCap);
        Array.Resize(ref _keyStart, newKeyCap);
        Array.Resize(ref _keyCount, newKeyCap);
        Array.Resize(ref _flatRows, newRowCap);
        _capacity = newKeyCap;
        _rowCapacity = newRowCap;
    }

    private static void ThrowBuildFailed() =>
        throw new InvalidOperationException(
            $"FrameLookup<{typeof(TKey).Name}>.Build failed after maximum grow attempts.");

    private static int CeilPow2(int n)
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
