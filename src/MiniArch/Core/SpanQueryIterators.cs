using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Extension methods on <see cref="Query"/> providing span-based iteration
/// over entity components using <c>foreach (ref var row in query.EachSpan&lt;T1&gt;())</c>.
/// <para/>
/// Convenient and type-safe, but constructs a <c>SpanEachRow</c> wrapper per row.
/// For maximum throughput, iterate <c>query.Chunks</c> directly and access
/// <see cref="Chunk.GetComponentSpan{T}"/> — that path has zero per-row overhead
/// and also supports unlimited component types.
/// </summary>
public static class SpanQueryExtensions
{
    /// <summary>Iterates entity ids only (no components).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEntities EachSpan(this Query query)
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with one component type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1> EachSpan<T1>(this Query query)
        where T1 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with two component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2> EachSpan<T1, T2>(this Query query)
        where T1 : struct
        where T2 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with three component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3> EachSpan<T1, T2, T3>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with four component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4> EachSpan<T1, T2, T3, T4>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with five component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5> EachSpan<T1, T2, T3, T4, T5>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with six component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5, T6> EachSpan<T1, T2, T3, T4, T5, T6>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        where T6 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with seven component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5, T6, T7> EachSpan<T1, T2, T3, T4, T5, T6, T7>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        where T6 : struct
        where T7 : struct
        => new(query.GetChunkSpan());

    /// <summary>Iterates entities with eight component types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> EachSpan<T1, T2, T3, T4, T5, T6, T7, T8>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        where T6 : struct
        where T7 : struct
        where T8 : struct
        => new(query.GetChunkSpan());
}

/// <summary>
/// Span-based iterator over entity ids (no component data).
/// Use via <c>foreach (var entity in query.EachSpan())</c>.
/// </summary>
public ref struct SpanEntities
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEntities(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEntities GetEnumerator() => this;

    /// <summary>Advances to the next entity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _rowIdx++;
        if (_rowIdx < _rowCount) return true;

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowIdx = 0;
            _rowCount = chunk.Count;
            _entities = chunk.GetEntities();
            return true;
        }
    }

    /// <summary>Gets the current entity.</summary>
    public Entity Current => _entities[_rowIdx];
}

/// <summary>Row yielded by <see cref="SpanEach{T1}"/> with one component reference.</summary>
public ref struct SpanEachRow<T1>
    where T1 : struct
{
    private ref T1 _r0;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, Entity entity)
    {
        _r0 = ref r0;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2}"/> with two component references.</summary>
public ref struct SpanEachRow<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2,T3}"/> with three component references.</summary>
public ref struct SpanEachRow<T1, T2, T3>
    where T1 : struct
    where T2 : struct
    where T3 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, ref T3 r2, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _r2 = ref r2;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2,T3,T4}"/> with four component references.</summary>
public ref struct SpanEachRow<T1, T2, T3, T4>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, ref T3 r2, ref T4 r3, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _r2 = ref r2;
        _r3 = ref r3;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2,T3,T4,T5}"/> with five component references.</summary>
public ref struct SpanEachRow<T1, T2, T3, T4, T5>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, ref T3 r2, ref T4 r3, ref T5 r4, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _r2 = ref r2;
        _r3 = ref r3;
        _r4 = ref r4;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2,T3,T4,T5,T6}"/> with six component references.</summary>
public ref struct SpanEachRow<T1, T2, T3, T4, T5, T6>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private ref T6 _r5;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, ref T3 r2, ref T4 r3, ref T5 r4, ref T6 r5, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _r2 = ref r2;
        _r3 = ref r3;
        _r4 = ref r4;
        _r5 = ref r5;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets a reference to the sixth component.</summary>
    public ref T6 Get5() => ref _r5;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2,T3,T4,T5,T6,T7}"/> with seven component references.</summary>
public ref struct SpanEachRow<T1, T2, T3, T4, T5, T6, T7>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
    where T7 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private ref T6 _r5;
    private ref T7 _r6;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, ref T3 r2, ref T4 r3, ref T5 r4, ref T6 r5, ref T7 r6, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _r2 = ref r2;
        _r3 = ref r3;
        _r4 = ref r4;
        _r5 = ref r5;
        _r6 = ref r6;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets a reference to the sixth component.</summary>
    public ref T6 Get5() => ref _r5;
    /// <summary>Gets a reference to the seventh component.</summary>
    public ref T7 Get6() => ref _r6;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>Row yielded by <see cref="SpanEach{T1,T2,T3,T4,T5,T6,T7,T8}"/> with eight component references.</summary>
public ref struct SpanEachRow<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
    where T7 : struct
    where T8 : struct
{
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private ref T6 _r5;
    private ref T7 _r6;
    private ref T8 _r7;
    private Entity _entity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEachRow(ref T1 r0, ref T2 r1, ref T3 r2, ref T4 r3, ref T5 r4, ref T6 r5, ref T7 r6, ref T8 r7, Entity entity)
    {
        _r0 = ref r0;
        _r1 = ref r1;
        _r2 = ref r2;
        _r3 = ref r3;
        _r4 = ref r4;
        _r5 = ref r5;
        _r6 = ref r6;
        _r7 = ref r7;
        _entity = entity;
    }

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets a reference to the sixth component.</summary>
    public ref T6 Get5() => ref _r5;
    /// <summary>Gets a reference to the seventh component.</summary>
    public ref T7 Get6() => ref _r6;
    /// <summary>Gets a reference to the eighth component.</summary>
    public ref T8 Get7() => ref _r7;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entity;
}

/// <summary>
/// Span-based iterator yielding <see cref="SpanEachRow{T1}"/> for each
/// entity matching the query. Use via <c>foreach (ref var row in query.EachSpan&lt;T1&gt;())</c>.
/// </summary>
public ref struct SpanEach<T1>
    where T1 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1> Current => new SpanEachRow<T1>(ref _r0, _entities[_rowIdx]);

    /// <summary>Gets a reference to the sole component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for two-component queries.</summary>
public ref struct SpanEach<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2> Current => new SpanEachRow<T1, T2>(ref _r0, ref _r1, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for three-component queries.</summary>
public ref struct SpanEach<T1, T2, T3>
    where T1 : struct
    where T2 : struct
    where T3 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2, T3> Current => new SpanEachRow<T1, T2, T3>(ref _r0, ref _r1, ref _r2, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for four-component queries.</summary>
public ref struct SpanEach<T1, T2, T3, T4>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _r3 = ref Unsafe.Add(ref _r3, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2, T3, T4> Current => new SpanEachRow<T1, T2, T3, T4>(ref _r0, ref _r1, ref _r2, ref _r3, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for five-component queries.</summary>
public ref struct SpanEach<T1, T2, T3, T4, T5>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _r3 = ref Unsafe.Add(ref _r3, 1);
            _r4 = ref Unsafe.Add(ref _r4, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            _r4 = ref chunk.GetComponentRef<T5>(Component<T5>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2, T3, T4, T5> Current => new SpanEachRow<T1, T2, T3, T4, T5>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for six-component queries.</summary>
public ref struct SpanEach<T1, T2, T3, T4, T5, T6>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private ref T6 _r5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
        _r5 = ref Unsafe.NullRef<T6>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _r3 = ref Unsafe.Add(ref _r3, 1);
            _r4 = ref Unsafe.Add(ref _r4, 1);
            _r5 = ref Unsafe.Add(ref _r5, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            _r4 = ref chunk.GetComponentRef<T5>(Component<T5>.ComponentType);
            _r5 = ref chunk.GetComponentRef<T6>(Component<T6>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2, T3, T4, T5, T6> Current => new SpanEachRow<T1, T2, T3, T4, T5, T6>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, ref _r5, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets a reference to the sixth component.</summary>
    public ref T6 Get5() => ref _r5;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for seven-component queries.</summary>
public ref struct SpanEach<T1, T2, T3, T4, T5, T6, T7>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
    where T7 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private ref T6 _r5;
    private ref T7 _r6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
        _r5 = ref Unsafe.NullRef<T6>();
        _r6 = ref Unsafe.NullRef<T7>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _r3 = ref Unsafe.Add(ref _r3, 1);
            _r4 = ref Unsafe.Add(ref _r4, 1);
            _r5 = ref Unsafe.Add(ref _r5, 1);
            _r6 = ref Unsafe.Add(ref _r6, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            _r4 = ref chunk.GetComponentRef<T5>(Component<T5>.ComponentType);
            _r5 = ref chunk.GetComponentRef<T6>(Component<T6>.ComponentType);
            _r6 = ref chunk.GetComponentRef<T7>(Component<T7>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2, T3, T4, T5, T6, T7> Current => new SpanEachRow<T1, T2, T3, T4, T5, T6, T7>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, ref _r5, ref _r6, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets a reference to the sixth component.</summary>
    public ref T6 Get5() => ref _r5;
    /// <summary>Gets a reference to the seventh component.</summary>
    public ref T7 Get6() => ref _r6;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}

/// <summary>Span-based iterator for eight-component queries.</summary>
public ref struct SpanEach<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
    where T6 : struct
    where T7 : struct
    where T8 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private int _rowIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;
    private ref T2 _r1;
    private ref T3 _r2;
    private ref T4 _r3;
    private ref T5 _r4;
    private ref T6 _r5;
    private ref T7 _r6;
    private ref T8 _r7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _rowIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
        _r5 = ref Unsafe.NullRef<T6>();
        _r6 = ref Unsafe.NullRef<T7>();
        _r7 = ref Unsafe.NullRef<T8>();
    }

    /// <summary>Gets the enumerator (by-ref struct returns self).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> GetEnumerator() => this;

    /// <summary>Advances to the next entity row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _r3 = ref Unsafe.Add(ref _r3, 1);
            _r4 = ref Unsafe.Add(ref _r4, 1);
            _r5 = ref Unsafe.Add(ref _r5, 1);
            _r6 = ref Unsafe.Add(ref _r6, 1);
            _r7 = ref Unsafe.Add(ref _r7, 1);
            _rowIdx++;
            return true;
        }

        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx >= _chunks.Length) return false;

            var chunk = _chunks[_chunkIdx];
            if (chunk.Count == 0) continue;

            _rowCount = chunk.Count;
            _remaining = _rowCount - 1;
            _entities = chunk.GetEntities();
            _rowIdx = 0;
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            _r4 = ref chunk.GetComponentRef<T5>(Component<T5>.ComponentType);
            _r5 = ref chunk.GetComponentRef<T6>(Component<T6>.ComponentType);
            _r6 = ref chunk.GetComponentRef<T7>(Component<T7>.ComponentType);
            _r7 = ref chunk.GetComponentRef<T8>(Component<T8>.ComponentType);
            return true;
        }
    }

    /// <summary>Gets the current row with component references.</summary>
    public SpanEachRow<T1, T2, T3, T4, T5, T6, T7, T8> Current => new SpanEachRow<T1, T2, T3, T4, T5, T6, T7, T8>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, ref _r5, ref _r6, ref _r7, _entities[_rowIdx]);

    /// <summary>Gets a reference to the first component.</summary>
    public ref T1 Get0() => ref _r0;
    /// <summary>Gets a reference to the second component.</summary>
    public ref T2 Get1() => ref _r1;
    /// <summary>Gets a reference to the third component.</summary>
    public ref T3 Get2() => ref _r2;
    /// <summary>Gets a reference to the fourth component.</summary>
    public ref T4 Get3() => ref _r3;
    /// <summary>Gets a reference to the fifth component.</summary>
    public ref T5 Get4() => ref _r4;
    /// <summary>Gets a reference to the sixth component.</summary>
    public ref T6 Get5() => ref _r5;
    /// <summary>Gets a reference to the seventh component.</summary>
    public ref T7 Get6() => ref _r6;
    /// <summary>Gets a reference to the eighth component.</summary>
    public ref T8 Get7() => ref _r7;
    /// <summary>Gets the current entity.</summary>
    public Entity Entity => _entities[_rowIdx];
}
