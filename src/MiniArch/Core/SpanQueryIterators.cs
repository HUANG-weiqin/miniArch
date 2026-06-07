using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Extension methods on <see cref="Query"/> providing span-based iteration
/// over entity components using <c>foreach (ref var row in query.EachSpan&lt;T1&gt;())</c>.
/// <para/>
/// Supports up to 4 component types (<c>T1</c>–<c>T4</c>).
/// For more components, use <see cref="ChunkViewEnumerable{T1}"/> with direct span access.
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
