using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MiniArch;

/// <summary>
/// Entity-only query facade that supports direct foreach enumeration
/// and chunk-level batch access.
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
    /// Returns an ordered entity enumerable using an internally pooled buffer per enumeration.
    /// </summary>
    public OrderedQuery OrderBy(IComparer<Entity> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        return new OrderedQuery(_query, comparer);
    }

    /// <summary>
    /// Returns an ordered entity enumerable using an internally pooled buffer per enumeration.
    /// </summary>
    public OrderedQuery OrderBy(Comparison<Entity> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        return OrderBy(Comparer<Entity>.Create(comparison));
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
        where TForEach : IChunkForEach
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
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEachChunkParallel(ChunkAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var chunks = _query.GetChunkViewArray(out var count);
        if (count == 0)
            return;

        var threads = Environment.ProcessorCount;
        if (count >= threads)
        {
            Parallel.For(0, count, i => action(chunks[i]));
            return;
        }

        // Fewer chunks than threads — split entity ranges within chunks.
        BuildEntityRangePartitions(chunks, count, threads, out var partitionArray, out var partitionCount);
        if (partitionCount == 0)
            return;
        if (partitionCount == 1)
        {
            action(partitionArray[0]);
            return;
        }

        Parallel.For(0, partitionCount, i => action(partitionArray[i]));
    }

    /// <summary>
    /// Iterates matched chunks in parallel using a struct <c>IChunkForEach</c>
    /// implementation. Same parallelism strategy as the delegate-based
    /// <see cref="ForEachChunkParallel(ChunkAction)"/> overload, but the inner
    /// <see cref="IChunkForEach.OnChunk"/> call is devirtualised to the
    /// concrete <typeparamref name="TForEach"/> type.
    /// <para>
    /// <typeparamref name="TForEach"/> is passed by value and captured into
    /// each <c>Parallel.For</c> worker. For stateful per-worker accumulation,
    /// use <c>[ThreadStatic]</c> fields inside the struct rather than mutating
    /// the struct itself.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ForEachChunkParallel<TForEach>(TForEach forEach)
        where TForEach : IChunkForEach
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
        if (arr != null && arr.Length >= minLength)
            return arr;
        arr = new ChunkView[minLength];
        t_partitions = arr;
        return arr;
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
/// <b>Parallel usage</b> (<c>in</c> parameter): the struct is copied into
/// each worker; per-worker accumulation must use <c>[ThreadStatic]</c>
/// fields inside the struct rather than mutating the struct itself, since
/// each worker observes its own copy.
/// </para>
/// </remarks>
public interface IChunkForEach
{
    /// <summary>
    /// Processes a single matched chunk. Reads/writes happen via
    /// <see cref="ChunkView.GetSpan{T}"/> / <see cref="ChunkView.GetComponentSpanAt{T}"/>.
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

            _entities = archetype.GetEntityStorage();
            _count = archetype.EntityCount;
            _rowIndex = -1;
        }
    }
}

/// <summary>
/// Entity-only ordered query facade that materializes and sorts results per enumeration.
/// </summary>
public readonly struct OrderedQuery
{
    private readonly MiniArch.Core.QueryCache _query;
    private readonly IComparer<Entity> _comparer;

    internal OrderedQuery(MiniArch.Core.QueryCache query, IComparer<Entity> comparer)
    {
        _query = query;
        _comparer = comparer;
    }

    /// <summary>
    /// Returns the struct enumerator used by foreach.
    /// </summary>
    public OrderedQueryEnumerator GetEnumerator() => new(_query, _comparer);
}

/// <summary>
/// Struct enumerator over sorted entities.
/// </summary>
public struct OrderedQueryEnumerator : IDisposable
{
    private readonly MiniArch.Core.QueryCache _query;
    private readonly IComparer<Entity> _comparer;
    private Entity[]? _entities;
    private int _count;
    private int _index;
    private bool _initialized;
    private Entity _current;

    internal OrderedQueryEnumerator(MiniArch.Core.QueryCache query, IComparer<Entity> comparer)
    {
        _query = query;
        _comparer = comparer;
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
    /// </summary>
    public bool MoveNext()
    {
        if (!_initialized)
        {
            Initialize();
        }

        _index++;
        if (_index >= _count)
        {
            return false;
        }

        _current = _entities![_index];
        return true;
    }

    /// <summary>
    /// Returns the rented sort buffer.
    /// </summary>
    public void Dispose()
    {
        var entities = _entities;
        if (entities is null)
        {
            return;
        }

        _entities = null;
        _count = 0;
        ArrayPool<Entity>.Shared.Return(entities);
    }

    private void Initialize()
    {
        _initialized = true;

        var archetypes = _query.GetArchetypeArray(out var archetypeCount);
        var count = 0;
        for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
        {
            count += archetypes[archetypeIndex].EntityCount;
        }

        if (count == 0)
        {
            return;
        }

        var entities = ArrayPool<Entity>.Shared.Rent(count);
        _entities = entities;
        _count = count;

        var entityIndex = 0;
        for (var archetypeIndex = 0; archetypeIndex < archetypeCount; archetypeIndex++)
        {
            var archetype = archetypes[archetypeIndex];
            var storage = archetype.GetEntityStorage();
            var rowCount = archetype.EntityCount;
            Array.Copy(storage, 0, entities, entityIndex, rowCount);
            entityIndex += rowCount;
        }

        Array.Sort(entities, 0, count, _comparer);
    }
}
