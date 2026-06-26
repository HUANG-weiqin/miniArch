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
    private readonly MiniArch.Core.Query _query;

    internal Query(MiniArch.Core.Query query)
    {
        _query = query;
    }

    /// <summary>
    /// Gets the underlying advanced query.
    /// </summary>
    internal MiniArch.Core.Query Advanced => _query;

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
        var subPerChunk = Math.Max(1, threads / count);
        var partitions = GetPartitionBuffer(threads);
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
                    partitions[pIdx++] = chunks[i].Slice(start, size);
                start += size;
            }
        }

        if (pIdx == 1)
            action(partitions[0]);
        else
            Parallel.For(0, pIdx, i => action(partitions[i]));
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

    internal QueryEnumerator(MiniArch.Core.Query query)
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
    private readonly MiniArch.Core.Query _query;
    private readonly IComparer<Entity> _comparer;

    internal OrderedQuery(MiniArch.Core.Query query, IComparer<Entity> comparer)
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
    private readonly MiniArch.Core.Query _query;
    private readonly IComparer<Entity> _comparer;
    private Entity[]? _entities;
    private int _count;
    private int _index;
    private bool _initialized;
    private Entity _current;

    internal OrderedQueryEnumerator(MiniArch.Core.Query query, IComparer<Entity> comparer)
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
