using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    private int _rowCount;
    private int _remaining;
    private int _entityIdx;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _entityIdx = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _entityIdx++;
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
            _entityIdx = 0;
            _entities = chunk.GetEntities();
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_entityIdx];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;
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
    private int _rowCount;
    private int _remaining;
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
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
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
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;
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
    private int _rowCount;
    private int _remaining;
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
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
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
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T3 Get2() => ref _r2;
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
    private int _rowCount;
    private int _remaining;
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
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
            _r1 = ref Unsafe.Add(ref _r1, 1);
            _r2 = ref Unsafe.Add(ref _r2, 1);
            _r3 = ref Unsafe.Add(ref _r3, 1);
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
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T3 Get2() => ref _r2;

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T4 Get3() => ref _r3;
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
    private int _rowCount;
    private int _remaining;
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
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            _r4 = ref chunk.GetComponentRef<T5>(Component<T5>.ComponentType);
            return true;
        }
    }

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T3 Get2() => ref _r2;

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T4 Get3() => ref _r3;

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T5 Get4() => ref _r4;
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
    private int _rowCount;
    private int _remaining;
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
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
        _r5 = ref Unsafe.NullRef<T6>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6> GetEnumerator() => this;

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
            _r0 = ref chunk.GetComponentRef<T1>(Component<T1>.ComponentType);
            _r1 = ref chunk.GetComponentRef<T2>(Component<T2>.ComponentType);
            _r2 = ref chunk.GetComponentRef<T3>(Component<T3>.ComponentType);
            _r3 = ref chunk.GetComponentRef<T4>(Component<T4>.ComponentType);
            _r4 = ref chunk.GetComponentRef<T5>(Component<T5>.ComponentType);
            _r5 = ref chunk.GetComponentRef<T6>(Component<T6>.ComponentType);
            return true;
        }
    }

    public SpanEach<T1, T2, T3, T4, T5, T6> Current => this;

    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T3 Get2() => ref _r2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T4 Get3() => ref _r3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T5 Get4() => ref _r4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T6 Get5() => ref _r5;
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
    private int _rowCount;
    private int _remaining;
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
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
        _r1 = ref Unsafe.NullRef<T2>();
        _r2 = ref Unsafe.NullRef<T3>();
        _r3 = ref Unsafe.NullRef<T4>();
        _r4 = ref Unsafe.NullRef<T5>();
        _r5 = ref Unsafe.NullRef<T6>();
        _r6 = ref Unsafe.NullRef<T7>();
    }

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5, T6, T7> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T3 Get2() => ref _r2;

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T4 Get3() => ref _r3;

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T5 Get4() => ref _r4;

    /// <summary>Gets the sixth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T6 Get5() => ref _r5;

    /// <summary>Gets the seventh component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T7 Get6() => ref _r6;
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
    private int _rowCount;
    private int _remaining;
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

    /// <summary>Returns this instance as the enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> GetEnumerator() => this;

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
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

    /// <summary>The current row state.</summary>
    public SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> Current => this;

    /// <summary>The current entity.</summary>
    public Entity Entity => _entities[_rowCount - 1 - _remaining];

    /// <summary>Gets the first component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T1 Get0() => ref _r0;

    /// <summary>Gets the second component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T2 Get1() => ref _r1;

    /// <summary>Gets the third component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T3 Get2() => ref _r2;

    /// <summary>Gets the fourth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T4 Get3() => ref _r3;

    /// <summary>Gets the fifth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T5 Get4() => ref _r4;

    /// <summary>Gets the sixth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T6 Get5() => ref _r5;

    /// <summary>Gets the seventh component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T7 Get6() => ref _r6;

    /// <summary>Gets the eighth component of the current row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T8 Get7() => ref _r7;
}
