using System.Runtime.CompilerServices;

namespace MiniArch;

/// <summary>
/// Frame-level derived index that maps a key to a contiguous span of <see cref="RowRef"/>
/// pointing into chunk storage. Build once per frame from a stable World snapshot,
/// then query any number of times.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use when</b>: you need to query thousands of keys per frame (grid cells, teams, zones)
/// and/or read component values from the results. The one-time <see cref="Build{TSel}"/> cost
/// is amortized across many subsequent key lookups.
/// </para>
/// <para>
/// <b>Not for</b>: occasional single-key lookups (a few queries per frame).
/// For that, use <see cref="ComponentBucketQuery{TComponent}"/> which requires no build step.
/// </para>
/// <para>
/// <b>Lifetime</b>: Build per frame, query within the same frame's snapshot, then Clear or rebuild.
/// Indexer results are valid until the next Build or Clear.
/// </para>
/// </remarks>
public sealed class FrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private const int DefaultKeyCapacity = 16;
    private const int DefaultRowCapacity = 64;

    /// <summary>Maximum representable capacity (power of two).</summary>
    internal const int MaxCapacity = 1 << 30;

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// A capacity is negative or exceeds <see cref="MaxCapacity"/>.
    /// </exception>
    public FrameLookup(int initialKeyCapacity = 0, int initialRowCapacity = 0)
    {
        ValidateCapacity(initialKeyCapacity, nameof(initialKeyCapacity));
        ValidateCapacity(initialRowCapacity, nameof(initialRowCapacity));
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
        _generation = unchecked(_generation + 1);
        if (_generation == 0)
        {
            Array.Clear(_stamps);
            _generation = 1;
        }
        _totalRows = 0;
        _keyCountTotal = 0;
    }

    /// <summary>
    /// Ensures the lookup has at least the specified capacity.
    /// Existing build results (if any) are preserved: the key-table is rehashed
    /// into the new capacity so that indexer spans remain valid.
    /// Call before <see cref="TryBuild{TSel}"/> if you know the expected counts
    /// in advance and want to avoid incremental growth during Build.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A capacity is negative or exceeds <see cref="MaxCapacity"/>.
    /// </exception>
    public void EnsureCapacity(int minKeyCapacity, int minRowCapacity)
    {
        ValidateCapacity(minKeyCapacity, nameof(minKeyCapacity));
        ValidateCapacity(minRowCapacity, nameof(minRowCapacity));

        var newKeyCapacity = CeilPow2(Math.Max(minKeyCapacity, _capacity));
        var newRowCapacity = CeilPow2(Math.Max(minRowCapacity, _rowCapacity));
        if (newKeyCapacity == _capacity && newRowCapacity == _rowCapacity)
            return;

        var newKeys = _keys;
        var newStamps = _stamps;
        var newStarts = _keyStart;
        var newCounts = _keyCount;

        if (newKeyCapacity > _capacity)
        {
            newKeys = new TKey[newKeyCapacity];
            newStamps = new int[newKeyCapacity];
            newStarts = new int[newKeyCapacity];
            newCounts = new int[newKeyCapacity];
            Rehash(newKeyCapacity, newKeys, newStamps, newStarts, newCounts);
        }

        var newRows = _flatRows;
        if (newRowCapacity > _rowCapacity)
        {
            newRows = new RowRef[newRowCapacity];
            Array.Copy(_flatRows, newRows, _totalRows);
        }

        // Publish only after every allocation and rehash has succeeded.
        _keys = newKeys;
        _stamps = newStamps;
        _keyStart = newStarts;
        _keyCount = newCounts;
        _capacity = newKeyCapacity;
        _flatRows = newRows;
        _rowCapacity = newRowCapacity;
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
    /// Grows internal storage as needed up to <see cref="MaxCapacity"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The data exceeds <see cref="MaxCapacity"/> and cannot be represented.</exception>
    public void Build<TSel>(World world, QueryDescription query, TSel selector = default)
        where TSel : struct, IFrameKeySelector<TKey>
    {
        ArgumentNullException.ThrowIfNull(world);

        while (!TryBuild(world, query, selector))
        {
            var newKeyCap = _capacity >= MaxCapacity / 2 ? MaxCapacity : _capacity * 2;
            var newRowCap = _rowCapacity >= MaxCapacity / 2 ? MaxCapacity : _rowCapacity * 2;

            if (newKeyCap <= _capacity && newRowCap <= _rowCapacity)
                ThrowBuildFailed();

            EnsureCapacity(newKeyCap, newRowCap);
        }
    }

    /// <summary>
    /// Tries to build using the current internal capacity.
    /// Returns false if capacity is exceeded — lookup is cleared on failure.
    /// If the caller-supplied <paramref name="selector"/>,
    /// <c>TKey.GetHashCode()</c>, or <c>IEquatable&lt;TKey&gt;.Equals()</c>
    /// throws, the lookup is cleared and the exception is rethrown so that
    /// partial state is never exposed.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public bool TryBuild<TSel>(World world, QueryDescription query, TSel selector = default)
        where TSel : struct, IFrameKeySelector<TKey>
    {
        ArgumentNullException.ThrowIfNull(world);

        try
        {
            return TryBuildCore(world, query, selector);
        }
        catch
        {
            Clear();
            throw;
        }
    }

    private bool TryBuildCore<TSel>(World world, QueryDescription query, TSel selector)
        where TSel : struct, IFrameKeySelector<TKey>
    {
        var q = world.Query(in query);
        var chunks = q.GetChunks();
        Clear();

        var tempCounts = GetScratchCounts();
        var distinctKeys = 0;
        var totalMatches = 0;

        // Copy selector for each pass so that a mutable-struct selector
        // (one whose Select mutates its own fields) produces the same
        // sequence in both passes.
        var countSelector = selector;
        var scatterSelector = selector;

        // Pass 1: count per key
        for (var ci = 0; ci < chunks.Length; ci++)
        {
            ref readonly var chunk = ref chunks[ci];
            var entities = chunk.GetEntities();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                totalMatches++;
                var key = countSelector.Select(entities[ri], chunks, ci, ri);
                var slot = FindOrCreateSlot(key, ref distinctKeys);
                if (slot < 0) { Clear(); return false; }
                tempCounts[slot]++;
            }
        }

        if (totalMatches > _rowCapacity)
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
                var key = scatterSelector.Select(entities[ri], chunks, ci, ri);
                var slot = FindSlot(key);
                if (slot < 0 || tempCounts[slot] <= 0)
                    ThrowUnstableSelector();
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
        var start = idx;
        do
        {
            if (_stamps[idx] != _generation) return -1;
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key)) return (int)idx;
            idx = (idx + 1) & mask;
        } while (idx != start);

        return -1;
    }

    /// <summary>
    /// Finds the slot for <paramref name="key"/>, creating it if absent.
    /// Probes the existing entries first so that a key already in the table
    /// is accepted even when <c>distinctKeys == _capacity</c>.
    /// </summary>
    private int FindOrCreateSlot(TKey key, ref int distinctKeys)
    {
        var mask = (uint)(_capacity - 1);
        var idx = (uint)key.GetHashCode() & mask;
        var start = idx;

        do
        {
            if (_stamps[idx] != _generation)
            {
                // Empty slot — insert here.
                _stamps[idx] = _generation;
                _keys[idx] = key;
                distinctKeys++;
                return (int)idx;
            }
            if (EqualityComparer<TKey>.Default.Equals(_keys[idx], key))
            {
                // Existing key — found.
                return (int)idx;
            }
            idx = (idx + 1) & mask;
        } while (idx != start);

        // Table is completely full (all slots occupied, key not found).
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int[] GetScratchCounts()
    {
        if (_scratchCounts == null || _scratchCounts.Length < _capacity)
        {
            _scratchCounts = new int[_capacity];
        }
        else
        {
            Array.Clear(_scratchCounts, 0, _capacity);
        }
        return _scratchCounts;
    }

    private void Rehash(
        int newCapacity,
        TKey[] newKeys,
        int[] newStamps,
        int[] newStarts,
        int[] newCounts)
    {
        var mask = (uint)(newCapacity - 1);
        for (var i = 0; i < _capacity; i++)
        {
            if (_stamps[i] != _generation)
                continue;

            var key = _keys[i];
            var index = (uint)key.GetHashCode() & mask;
            while (newStamps[index] == _generation)
                index = (index + 1) & mask;

            newStamps[index] = _generation;
            newKeys[index] = key;
            newStarts[index] = _keyStart[i];
            newCounts[index] = _keyCount[i];
        }
    }

    private static void ThrowBuildFailed() =>
        throw new InvalidOperationException(
            $"FrameLookup<{typeof(TKey).Name}>.Build failed: " +
            $"both key and row capacities have reached the representable limit ({MaxCapacity}).");

    private static void ThrowUnstableSelector() =>
        throw new InvalidOperationException(
            $"FrameLookup<{typeof(TKey).Name}>.TryBuild requires the selector to return " +
            "the same key for each row in both build passes.");

    private static void ValidateCapacity(int capacity, string paramName)
    {
        if ((uint)capacity > MaxCapacity)
        {
            throw new ArgumentOutOfRangeException(
                paramName, capacity, $"Capacity must be between 0 and {MaxCapacity}.");
        }
    }

    private static int CeilPow2(int n)
    {
        if (n <= 0) return 1;
        if (n > MaxCapacity)
            throw new ArgumentOutOfRangeException(nameof(n), n,
                $"Value exceeds maximum capacity of {MaxCapacity}.");
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }
}
