using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Provides zero-allocation span-based iteration over matched query chunks.
/// </summary>
public static class SpanQueryExtensions
{
    /// <summary>Enumerates entities only, no component spans.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEntities EachSpan(this Query query)
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 1 component span. Access via <c>row.Entity</c> and <c>row.Get0()</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1> EachSpan<T1>(this Query query)
        where T1 : struct
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 2 component spans. Access via <c>row.Get0()</c>..<c>row.Get1()</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2> EachSpan<T1, T2>(this Query query)
        where T1 : struct
        where T2 : struct
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 3 component spans.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3> EachSpan<T1, T2, T3>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 4 component spans.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4> EachSpan<T1, T2, T3, T4>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 5 component spans.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5> EachSpan<T1, T2, T3, T4, T5>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 6 component spans.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5, T6> EachSpan<T1, T2, T3, T4, T5, T6>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        where T6 : struct
        => new(query.GetChunkSpan());

    /// <summary>Enumerates entities with 7 component spans.</summary>
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

    /// <summary>Enumerates entities with 8 component spans. For 9+ components, use raw chunk iteration.</summary>
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
/// Zero-allocation entity-only enumerator over matched query chunks.
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

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEntities GetEnumerator() => this;

    /// <summary>Advances to the next entity. Returns false when exhausted.</summary>
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

    /// <summary>The current entity.</summary>
    public Entity Current => _entities[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 1 component. Use <c>row.Entity</c> and <c>row.Get0()</c>.
/// </summary>
public ref struct SpanEach<T1>
    where T1 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 2 components.
/// </summary>
public ref struct SpanEach<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 3 components.
/// </summary>
public ref struct SpanEach<T1, T2, T3>
    where T1 : struct
    where T2 : struct
    where T3 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;
    private ReadOnlySpan<T3> _s2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
        _s2 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            _s2 = chunk.GetComponentSpan<T3>(Component<T3>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T3 Get2() => ref _s2[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 4 components.
/// </summary>
public ref struct SpanEach<T1, T2, T3, T4>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;
    private ReadOnlySpan<T3> _s2;
    private ReadOnlySpan<T4> _s3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
        _s2 = default;
        _s3 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            _s2 = chunk.GetComponentSpan<T3>(Component<T3>.ComponentType);
            _s3 = chunk.GetComponentSpan<T4>(Component<T4>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T3 Get2() => ref _s2[_rowIdx];

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T4 Get3() => ref _s3[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 5 components.
/// </summary>
public ref struct SpanEach<T1, T2, T3, T4, T5>
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct
    where T5 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;
    private ReadOnlySpan<T3> _s2;
    private ReadOnlySpan<T4> _s3;
    private ReadOnlySpan<T5> _s4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
        _s2 = default;
        _s3 = default;
        _s4 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            _s2 = chunk.GetComponentSpan<T3>(Component<T3>.ComponentType);
            _s3 = chunk.GetComponentSpan<T4>(Component<T4>.ComponentType);
            _s4 = chunk.GetComponentSpan<T5>(Component<T5>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T3 Get2() => ref _s2[_rowIdx];

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T4 Get3() => ref _s3[_rowIdx];

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T5 Get4() => ref _s4[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 6 components.
/// </summary>
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
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;
    private ReadOnlySpan<T3> _s2;
    private ReadOnlySpan<T4> _s3;
    private ReadOnlySpan<T5> _s4;
    private ReadOnlySpan<T6> _s5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
        _s2 = default;
        _s3 = default;
        _s4 = default;
        _s5 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            _s2 = chunk.GetComponentSpan<T3>(Component<T3>.ComponentType);
            _s3 = chunk.GetComponentSpan<T4>(Component<T4>.ComponentType);
            _s4 = chunk.GetComponentSpan<T5>(Component<T5>.ComponentType);
            _s5 = chunk.GetComponentSpan<T6>(Component<T6>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5, T6> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T3 Get2() => ref _s2[_rowIdx];

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T4 Get3() => ref _s3[_rowIdx];

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T5 Get4() => ref _s4[_rowIdx];

    /// <summary>Gets the sixth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T6 Get5() => ref _s5[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 7 components.
/// </summary>
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
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;
    private ReadOnlySpan<T3> _s2;
    private ReadOnlySpan<T4> _s3;
    private ReadOnlySpan<T5> _s4;
    private ReadOnlySpan<T6> _s5;
    private ReadOnlySpan<T7> _s6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
        _s2 = default;
        _s3 = default;
        _s4 = default;
        _s5 = default;
        _s6 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            _s2 = chunk.GetComponentSpan<T3>(Component<T3>.ComponentType);
            _s3 = chunk.GetComponentSpan<T4>(Component<T4>.ComponentType);
            _s4 = chunk.GetComponentSpan<T5>(Component<T5>.ComponentType);
            _s5 = chunk.GetComponentSpan<T6>(Component<T6>.ComponentType);
            _s6 = chunk.GetComponentSpan<T7>(Component<T7>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5, T6, T7> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T3 Get2() => ref _s2[_rowIdx];

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T4 Get3() => ref _s3[_rowIdx];

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T5 Get4() => ref _s4[_rowIdx];

    /// <summary>Gets the sixth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T6 Get5() => ref _s5[_rowIdx];

    /// <summary>Gets the seventh component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T7 Get6() => ref _s6[_rowIdx];
}

/// <summary>
/// Zero-allocation span enumerator with 8 components. For 9+, use raw chunk iteration.
/// </summary>
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
    private int _rowIdx;
    private int _rowCount;
    private ReadOnlySpan<Entity> _entities;
    private ReadOnlySpan<T1> _s0;
    private ReadOnlySpan<T2> _s1;
    private ReadOnlySpan<T3> _s2;
    private ReadOnlySpan<T4> _s3;
    private ReadOnlySpan<T5> _s4;
    private ReadOnlySpan<T6> _s5;
    private ReadOnlySpan<T7> _s6;
    private ReadOnlySpan<T8> _s7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowIdx = -1;
        _rowCount = 0;
        _entities = default;
        _s0 = default;
        _s1 = default;
        _s2 = default;
        _s3 = default;
        _s4 = default;
        _s5 = default;
        _s6 = default;
        _s7 = default;
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _s0 = chunk.GetComponentSpan<T1>(Component<T1>.ComponentType);
            _s1 = chunk.GetComponentSpan<T2>(Component<T2>.ComponentType);
            _s2 = chunk.GetComponentSpan<T3>(Component<T3>.ComponentType);
            _s3 = chunk.GetComponentSpan<T4>(Component<T4>.ComponentType);
            _s4 = chunk.GetComponentSpan<T5>(Component<T5>.ComponentType);
            _s5 = chunk.GetComponentSpan<T6>(Component<T6>.ComponentType);
            _s6 = chunk.GetComponentSpan<T7>(Component<T7>.ComponentType);
            _s7 = chunk.GetComponentSpan<T8>(Component<T8>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T1 Get0() => ref _s0[_rowIdx];

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T2 Get1() => ref _s1[_rowIdx];

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T3 Get2() => ref _s2[_rowIdx];

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T4 Get3() => ref _s3[_rowIdx];

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T5 Get4() => ref _s4[_rowIdx];

    /// <summary>Gets the sixth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T6 Get5() => ref _s5[_rowIdx];

    /// <summary>Gets the seventh component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T7 Get6() => ref _s6[_rowIdx];

    /// <summary>Gets the eighth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T8 Get7() => ref _s7[_rowIdx];
}
