using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MiniArch;

/// <summary>
/// Entity-only query facade that supports direct foreach enumeration
/// and chunk-level batch access.
///
/// <para><b>Iteration order contract</b> (promoted to semantic guarantee):</para>
/// <list type="bullet">
///   <item><b>Archetype order</b> — matched archetypes are sorted
///   lexicographically by their component-type signatures.</item>
///   <item><b>Entity order within an archetype</b> — entities are iterated
///   in their physical storage order: new entities are appended to the end;
///   entity removal (Destroy / component removal) uses swap-remove (the last
///   entity fills the vacated slot), which reorders survivors deterministically.</item>
///   <item><b>All access paths consistent</b> — <c>foreach</c>,
///   <see cref="GetChunks"/> → <see cref="ChunkView.GetEntities"/>,
///   and <c>Advanced.GetArchetypeSpan</c> → <c>archetype.GetEntities</c>
///   all produce the same entity order.</item>
///   <item><b>Deterministic</b> — given the same sequence of structural
///   changes, the iteration order is byte-for-byte identical (verified
///   by <c>QueryOrderingTests</c>).</item>
/// </list>
/// <para>
/// If you need an iteration order that is independent of structural change
/// history, use <see cref="OrderByEntityId"/>,
/// <see cref="OrderByEntityIdDescending"/>, or
/// <see cref="OrderByComponent{T}(Comparison{T})"/>.
/// </para>
/// </summary>
public readonly struct Query
{
    private readonly MiniArch.Core.QueryCache _query;

    internal Query(MiniArch.Core.QueryCache query)
    {
        _query = query;
    }

    /// <summary>
    /// Gets the underlying advanced query.
    /// </summary>
    internal MiniArch.Core.QueryCache Advanced => _query;

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public QueryEnumerator GetEnumerator() => new(_query);

    /// <summary>
    /// Returns entities sorted by <see cref="Entity.Id"/> using an internally pooled buffer.
    /// </summary>
    public OrderedEntityQuery OrderByEntityId() => new OrderedEntityQuery(_query, descending: false);

    /// <summary>
    /// Returns entities sorted by <see cref="Entity.Id"/> in descending order.
    /// </summary>
    public OrderedEntityQuery OrderByEntityIdDescending() => new OrderedEntityQuery(_query, descending: true);

    /// <summary>
    /// Returns entities sorted by component <typeparamref name="T"/>.
    /// Reads all T values in a single linear scan for cache-friendly sorting.
    /// </summary>
    public OrderedComponentQuery<T> OrderByComponent<T>(Comparison<T> comparison)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return new OrderedComponentQuery<T>(_query, comparison, descending: false);
    }

    /// <summary>
    /// Returns entities sorted by component <typeparamref name="T"/> in descending order.
    /// </summary>
    public OrderedComponentQuery<T> OrderByComponentDescending<T>(Comparison<T> comparison)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return new OrderedComponentQuery<T>(_query, comparison, descending: true);
    }

    /// <summary>
    /// Returns entities sorted by component <typeparamref name="T"/>
    /// using a reusable <see cref="IComparer{T}"/>.
    /// </summary>
    public OrderedComponentQuery<T> OrderByComponent<T>(IComparer<T> comparer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(comparer);
        return new OrderedComponentQuery<T>(_query, comparer.Compare, descending: false);
    }

    /// <summary>
    /// Returns entities sorted by component <typeparamref name="T"/> in descending order
    /// using a reusable <see cref="IComparer{T}"/>.
    /// </summary>
    public OrderedComponentQuery<T> OrderByComponentDescending<T>(IComparer<T> comparer)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(comparer);
        return new OrderedComponentQuery<T>(_query, comparer.Compare, descending: true);
    }

    // ================================================================
    //  Batch / chunk-level API (replaces .Advanced access)
    // ================================================================

    /// <summary>
    /// Gets matched chunks as a span for batch component access.
    /// Use <c>foreach (var chunk in query.GetChunks())</c>.
    /// Zero-copy, JIT-optimized span iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ChunkView> GetChunks() => _query.GetChunkViewSpan();

    /// <summary>
    /// Gets the refresh count (diagnostic).
    /// </summary>
    public int RefreshCount => _query.RefreshCount;

    // ================================================================
    //  Chunk-level iteration (sequential + parallel)
    // ================================================================

    /// <summary>
    /// Iterates matched chunks sequentially. Zero-alloc if the delegate is cached by the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEachChunk(ChunkAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var chunks = _query.GetChunkViewSpan();
        for (var i = 0; i < chunks.Length; i++)
            action(chunks[i]);
    }

    /// <summary>
    /// Iterates matched chunks sequentially using a struct <c>IChunkForEach</c>
    /// implementation. The JIT specialises the call site for the concrete
    /// <typeparamref name="TForEach"/> type, devirtualising
    /// <see cref="IChunkForEach.OnChunk"/> and removing the delegate
    /// allocation that the <see cref="ChunkAction"/>-based overload incurs
    /// when its lambda is not cached.
    /// <para>
    /// <typeparamref name="TForEach"/> is passed by <c>ref</c> so that
    /// mutable accumulator fields on the struct are visible across chunks:
    /// <code>
    /// var job = new SumJob();
    /// query.ForEachChunk(ref job);
    /// return job.Total;
    /// </code>
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEachChunk<TForEach>(ref TForEach forEach)
        where TForEach : struct, IChunkForEach
    {
        var chunks = _query.GetChunkViewSpan();
        for (var i = 0; i < chunks.Length; i++)
            forEach.OnChunk(chunks[i]);
    }

    /// <summary>
    /// Iterates matched chunks in parallel across worker threads.
    /// When chunk count is lower than processor count, entities within
    /// chunks are split into sub-ranges for finer-grained parallelism.
    /// Safe for component value reads/writes via <c>chunk.GetSpan&lt;T&gt;()</c>.
    /// NOT safe for structural changes (Add/Remove/Create/Destroy) — collect entity ids
    /// and apply via <see cref="MiniArch.Core.CommandStream"/> after this call returns.
    /// <para>
    /// This overload delegates to <see cref="ForEachChunkParallel{TForEach}(TForEach)"/>
    /// via the <see cref="ActionChunkForEach"/> adapter, so the parallelism
    /// logic is not duplicated.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEachChunkParallel(ChunkAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ForEachChunkParallel(new ActionChunkForEach(action));
    }

    /// <summary>
    /// Iterates matched chunks in parallel using a struct <c>IChunkForEach</c>
    /// implementation. Same parallelism strategy as the delegate-based
    /// <see cref="ForEachChunkParallel(ChunkAction)"/> overload, but the inner
    /// <see cref="IChunkForEach.OnChunk"/> call is devirtualised to the
    /// concrete <typeparamref name="TForEach"/> type.
    /// <para>
    /// <typeparamref name="TForEach"/> is passed by value and captured into the
    /// <c>Parallel.For</c> closure — all workers share the same captured copy.
    /// Mutating struct fields from <see cref="IChunkForEach.OnChunk"/> is a
    /// data race on the closure copy and the caller's original variable is never
    /// updated. To produce visible results, write to external shared state (e.g.
    /// <c>ConcurrentBag&lt;T&gt;</c>), thread-local storage with explicit merge,
    /// or a thread-safe collector.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEachChunkParallel<TForEach>(TForEach forEach)
        where TForEach : struct, IChunkForEach
    {
        var chunks = _query.GetChunkViewArray(out var count);
        if (count == 0)
            return;

        var threads = Environment.ProcessorCount;
        if (count >= threads)
        {
            Parallel.For(0, count, i => forEach.OnChunk(chunks[i]));
            return;
        }

        BuildEntityRangePartitions(chunks, count, threads, out var partitionArray, out var partitionCount);
        if (partitionCount == 0)
            return;
        if (partitionCount == 1)
        {
            forEach.OnChunk(partitionArray[0]);
            return;
        }

        Parallel.For(0, partitionCount, i => forEach.OnChunk(partitionArray[i]));
    }

    // Splits chunks into N entity-range subviews when chunk count is below
    // processor count, so Parallel.For still has enough independent units
    // of work. Returns the pooled buffer (caller owns lifetime via
    // ThreadStatic reuse) and the live count via out parameters.
    private static void BuildEntityRangePartitions(
        ChunkView[] chunks, int count, int threads,
        out ChunkView[] partitionArray, out int partitionCount)
    {
        var subPerChunk = Math.Max(1, threads / count);
        partitionArray = GetPartitionBuffer(threads);
        var pIdx = 0;
        for (var i = 0; i < count; i++)
        {
            var entities = chunks[i].Count;
            if (entities == 0) continue;
            var per = entities / subPerChunk;
            var rem = entities % subPerChunk;
            var start = 0;
            for (var j = 0; j < subPerChunk; j++)
            {
                var size = per + (j < rem ? 1 : 0);
                if (size > 0)
                    partitionArray[pIdx++] = chunks[i].Slice(start, size);
                start += size;
            }
        }

        partitionCount = pIdx;
    }

    [ThreadStatic]
    private static ChunkView[]? t_partitions;

    private static ChunkView[] GetPartitionBuffer(int minLength)
    {
        var arr = t_partitions;
        if (arr is not null && arr.Length >= minLength)
            return arr;
        arr = new ChunkView[minLength];
        t_partitions = arr;
        return arr;
    }

    /// <summary>
    /// Adapter that bridges <see cref="ChunkAction"/> delegates into
    /// <see cref="IChunkForEach"/>, enabling the delegate-based
    /// <see cref="ForEachChunkParallel(ChunkAction)"/> to reuse the
    /// struct-generic parallelism logic without duplicating it.
    /// </summary>
    private readonly struct ActionChunkForEach(ChunkAction _action) : IChunkForEach
    {
        public void OnChunk(ChunkView chunk) => _action(chunk);
    }
}

/// <summary>Delegate for chunk-level iteration.</summary>
public delegate void ChunkAction(ChunkView chunk);

/// <summary>
/// Zero-allocation chunk-level iteration interface. Implement this on a
/// <c>struct</c> and pass an instance to
/// <see cref="Query.ForEachChunk{TForEach}(ref TForEach)"/> or
/// <see cref="Query.ForEachChunkParallel{TForEach}(TForEach)"/>: the JIT
/// specialises the call site for the concrete struct type, devirtualising
/// <see cref="OnChunk"/> and eliding the delegate allocation that the
/// <see cref="ChunkAction"/>-based overloads incur when their lambda is not
/// cached by the caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequential usage</b> (<c>ref</c> parameter): the job struct is passed
/// by reference, so mutable fields on the struct are visible across chunks
/// (useful for accumulators).
/// </para>
/// <para>
/// <b>Parallel usage</b> (by-value parameter): the struct is captured by value
/// into the <c>Parallel.For</c> closure — all workers share the same captured
/// copy. Mutating fields from <see cref="OnChunk"/> is a data race on the
/// closure copy and the caller&#39;s variable is never updated. To produce visible
/// results, write to external shared state, thread-local storage with explicit
/// merge, or a thread-safe collector.
/// </para>
/// </remarks>
public interface IChunkForEach
{
    /// <summary>
    /// Processes a single matched chunk. Reads/writes happen via
    /// <see cref="ChunkView.GetSpan{T}"/> / <see cref="ChunkView.UnsafeGetComponentSpanAt{T}"/>.
    /// </summary>
    void OnChunk(ChunkView chunk);
}

/// <summary>
/// Struct enumerator over entities.
/// </summary>
public struct QueryEnumerator
{
    private readonly MiniArch.Core.Archetype[] _archetypes;
    private readonly int _archetypeCount;
    private int _archetypeIndex;
    private int _rowIndex;
    private Entity[]? _entities;
    private int _count;
    private Entity _current;

    internal QueryEnumerator(MiniArch.Core.QueryCache query)
    {
        _archetypes = query.GetArchetypeArray(out var archetypeCount);
        _archetypeCount = archetypeCount;
        _archetypeIndex = -1;
        _rowIndex = -1;
        _entities = null;
        _count = 0;
        _current = default;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Current => _current;

    /// <summary>
    /// Advances to the next entity.
    /// </summary>
    public bool MoveNext()
    {
        while (true)
        {
            if (_entities is not null)
            {
                _rowIndex++;
                if (_rowIndex < _count)
                {
                    _current = _entities[_rowIndex];
                    return true;
                }
            }

            _archetypeIndex++;
            if (_archetypeIndex >= _archetypeCount)
            {
                return false;
            }

            var archetype = _archetypes[_archetypeIndex];
            if (archetype.EntityCount == 0)
            {
                _entities = null;
                continue;
            }

            _entities = archetype.GetEntityStorageUnsafe();
            _count = archetype.EntityCount;
            _rowIndex = -1;
        }
    }
}

/// <summary>
/// Entity-only ordered query facade that materializes and sorts results by <see cref="Entity.Id"/> per enumeration.
/// </summary>
public readonly struct OrderedEntityQuery
{
    private readonly MiniArch.Core.QueryCache _query;
    private readonly bool _descending;

    internal OrderedEntityQuery(MiniArch.Core.QueryCache query, bool descending)
    {
        _query = query;
        _descending = descending;
    }

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public OrderedEntityEnumerator GetEnumerator() => new(_query, _descending);
}

/// <summary>
/// Struct enumerator over entities sorted by <see cref="Entity.Id"/>.
/// Uses fingerprint-based sort cache on the QueryCache:
///   - count pre-check (fast-fail for structural changes)
///   - salted SplitMix64 fingerprint for identity changes
///   - hit: returns cached sorted array, no allocation
///   - miss: materialize + sort + cache
/// Cache stores ascending; descending iterates in reverse.
/// </summary>
public struct OrderedEntityEnumerator : IDisposable
{
    private readonly MiniArch.Core.QueryCache _query;
    private readonly bool _descending;
    private Entity[]? _entities;
    private int _count;
    private int _index;
    private bool _initialized;
    private Entity _current;

    internal OrderedEntityEnumerator(MiniArch.Core.QueryCache query, bool descending)
    {
        _query = query;
        _descending = descending;
        _entities = null;
        _count = 0;
        _index = -1;
        _initialized = false;
        _current = default;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Current => _current;

    /// <summary>
    /// Advances to the next sorted entity.
    /// Descending direction iterates backward from the end,
    /// so no Array.Reverse is needed.
    /// </summary>
    public bool MoveNext()
    {
        if (!_initialized)
            Initialize();

        if (_descending)
        {
            _index--;
            if (_index < 0)
                return false;
        }
        else
        {
            _index++;
            if (_index >= _count)
                return false;
        }

        _current = _entities![_index];
        return true;
    }

    /// <summary>
    /// Releases this enumerator's reference to the entity array.
    /// The array itself is owned by the QueryCache (cached sort result).
    /// </summary>
    public void Dispose()
    {
        _entities = null;
        _count = 0;
        _index = -1;
        _current = default;
    }

    private void Initialize()
    {
        _initialized = true;

        var archetypes = _query.GetArchetypeArray(out var archetypeCount);
        var count = 0;
        for (var i = 0; i < archetypeCount; i++)
            count += archetypes[i].EntityCount;

        if (count == 0)
            return;

        // ── Try fingerprint cache ──
        if (_query.TryGetEntityIdSortCache(archetypes, archetypeCount, count,
                out var cachedEntities, out var cachedCount))
        {
            _entities = cachedEntities;
            _count = cachedCount;
            _index = _descending ? cachedCount : -1;
            return;
        }

        // ── Miss: materialize + sort (ascending, for cache) ──
        var entities = GC.AllocateUninitializedArray<Entity>(count);
        _entities = entities;
        _count = count;

        var entityIndex = 0;
        for (var i = 0; i < archetypeCount; i++)
        {
            var archetype = archetypes[i];
            var storage = archetype.GetEntityStorageUnsafe();
            var rowCount = archetype.EntityCount;
            Array.Copy(storage, 0, entities, entityIndex, rowCount);
            entityIndex += rowCount;
        }

        Array.Sort(entities, 0, count);

        // Store ascending in cache (descending handled by MoveNext direction)
        _query.SetEntityIdSortCache(archetypes, archetypeCount, count, entities);

        _index = _descending ? count : -1;
    }
}

/// <summary>
/// Entity query facade that sorts matching entities by component <typeparamref name="T"/>.
/// Internally batch-reads all T values in a single linear scan per archetype,
/// then sorts using the provided comparison.
/// </summary>
public readonly struct OrderedComponentQuery<T> where T : unmanaged
{
    private readonly MiniArch.Core.QueryCache _query;
    private readonly Comparison<T> _comparison;
    private readonly bool _descending;

    internal OrderedComponentQuery(MiniArch.Core.QueryCache query, Comparison<T> comparison, bool descending)
    {
        _query = query;
        _comparison = comparison;
        _descending = descending;
    }

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public OrderedComponentEnumerator<T> GetEnumerator() => new(_query, _comparison, _descending);
}

/// <summary>
/// Struct enumerator over entities sorted by component <typeparamref name="T"/>.
/// </summary>
public struct OrderedComponentEnumerator<T> : IDisposable where T : unmanaged
{
    private readonly MiniArch.Core.QueryCache _query;
    private readonly Comparison<T> _comparison;
    private readonly bool _descending;
    private Entity[]? _entities;
    private T[]? _values;
    private int _count;
    private int _index;
    private bool _initialized;
    private Entity _current;

    internal OrderedComponentEnumerator(MiniArch.Core.QueryCache query, Comparison<T> comparison, bool descending)
    {
        _query = query;
        _comparison = comparison;
        _descending = descending;
        _entities = null;
        _values = null;
        _count = 0;
        _index = -1;
        _initialized = false;
        _current = default;
    }

    /// <summary>
    /// Gets the current entity.
    /// </summary>
    public Entity Current => _current;

    /// <summary>
    /// Advances to the next sorted entity.
    /// </summary>
    public bool MoveNext()
    {
        if (!_initialized)
            Initialize();

        _index++;
        if (_index >= _count)
            return false;

        _current = _entities![_index];
        return true;
    }

    /// <summary>
    /// Returns the rented sort buffers.
    /// </summary>
    public void Dispose()
    {
        var entities = _entities;
        var values = _values;
        if (entities is not null)
        {
            _entities = null;
            _count = 0;
            ArrayPool<Entity>.Shared.Return(entities);
        }
        if (values is not null)
        {
            _values = null;
            ArrayPool<T>.Shared.Return(values);
        }
    }

    private void Initialize()
    {
        _initialized = true;

        var archetypes = _query.GetArchetypeArray(out var archetypeCount);
        var count = 0;
        for (var i = 0; i < archetypeCount; i++)
            count += archetypes[i].EntityCount;

        if (count == 0)
            return;

        var componentType = Core.Component<T>.ComponentType;

        // Fail-fast: all matched archetypes must contain component T.
        for (var i = 0; i < archetypeCount; i++)
        {
            if (!archetypes[i].TryGetComponentIndex(componentType, out _))
            {
                throw new InvalidOperationException(
                    $"Cannot sort by '{typeof(T).Name}': archetype {i} lacks this component. " +
                    $"Add .With<{typeof(T).Name}>() to the QueryDescription.");
            }
        }

        var entities = ArrayPool<Entity>.Shared.Rent(count);
        var values = ArrayPool<T>.Shared.Rent(count);
        _entities = entities;
        _values = values;
        _count = count;

        var index = 0;
        for (var i = 0; i < archetypeCount; i++)
        {
            var archetype = archetypes[i];
            var rowCount = archetype.EntityCount;
            if (rowCount == 0)
                continue;

            var entitySpan = archetype.GetEntityStorageUnsafe();
            entitySpan.AsSpan(0, rowCount).CopyTo(entities.AsSpan(index));

            var columnIndex = archetype.GetComponentIndex(componentType);
            var valueIndex = index;
            foreach (var chunk in archetype.AsChunkViews())
            {
                var componentSpan = chunk.UnsafeGetComponentSpanAt<T>(columnIndex);
                componentSpan.CopyTo(values.AsSpan(valueIndex));
                valueIndex += componentSpan.Length;
            }

            index += rowCount;
        }

        // Sort values as keys, rearranging entities accordingly.
        var comparison = _comparison; // local copy avoids CS1673 in struct
        var comparer = ComparisonCache<T>.Acquire(comparison, _descending);
        Array.Sort(values, entities, 0, count, comparer);
    }
}

/// <summary>
/// Factory for <see cref="IComparer{T}"/> wrappers around <see cref="Comparison{T}"/>.
/// Returns a new immutable <see cref="ComparisonComparer"/> per call — the allocation
/// is negligible compared to the O(n log n) sort and the pooled arrays it rents.
/// Previously used a <c>[ThreadStatic]</c> cache with mutable fields, but the
/// mutable-reuse pattern broke the <see cref="IComparer{T}"/> immutability contract
/// and risked corruption under same-thread reentrant sorting (outer sort callback
/// triggering an inner sort that overwrites the shared comparer's state).
/// </summary>
internal static class ComparisonCache<T>
{
    internal static IComparer<T> Acquire(Comparison<T> comparison, bool descending)
    {
        return new ComparisonComparer(comparison, descending);
    }

    private sealed class ComparisonComparer : IComparer<T>
    {
        private readonly Comparison<T> _comparison;
        private readonly bool _descending;

        public ComparisonComparer(Comparison<T> comparison, bool descending)
        {
            _comparison = comparison;
            _descending = descending;
        }

        public int Compare(T? x, T? y)
        {
            return _descending ? _comparison(y!, x!) : _comparison(x!, y!);
        }
    }
}
