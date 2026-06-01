using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public static class SpanQueryExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEntities EachSpan(this Query query)
        => new(query.GetChunkSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1> EachSpan<T1>(this Query query)
        where T1 : struct
        => new(query.GetChunkSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2> EachSpan<T1, T2>(this Query query)
        where T1 : struct
        where T2 : struct
        => new(query.GetChunkSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3> EachSpan<T1, T2, T3>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        => new(query.GetChunkSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4> EachSpan<T1, T2, T3, T4>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        => new(query.GetChunkSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5> EachSpan<T1, T2, T3, T4, T5>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        => new(query.GetChunkSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpanEach<T1, T2, T3, T4, T5, T6> EachSpan<T1, T2, T3, T4, T5, T6>(this Query query)
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
        where T5 : struct
        where T6 : struct
        => new(query.GetChunkSpan());

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEntities GetEnumerator() => this;

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

    public Entity Current => _entities[_rowIdx];
}

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

    public ref T1 Get0() => ref _r0;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public ref T6 Get5() => ref _r5;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public ref T6 Get5() => ref _r5;
    public ref T7 Get6() => ref _r6;
    public Entity Entity => _entity;
}

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

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public ref T6 Get5() => ref _r5;
    public ref T7 Get6() => ref _r6;
    public ref T8 Get7() => ref _r7;
    public Entity Entity => _entity;
}

public ref struct SpanEach<T1>
    where T1 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;
    private int _rowCount;
    private int _remaining;
    private ReadOnlySpan<Entity> _entities;
    private ref T1 _r0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpanEach(ReadOnlySpan<Chunk> chunks)
    {
        _chunks = chunks;
        _chunkIdx = -1;
        _rowCount = 0;
        _remaining = 0;
        _entities = default;
        _r0 = ref Unsafe.NullRef<T1>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1> GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (--_remaining >= 0)
        {
            _r0 = ref Unsafe.Add(ref _r0, 1);
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
            return true;
        }
    }

    public SpanEachRow<T1> Current => new SpanEachRow<T1>(ref _r0, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2> GetEnumerator() => this;

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

    public SpanEachRow<T1, T2> Current => new SpanEachRow<T1, T2>(ref _r0, ref _r1, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3> GetEnumerator() => this;

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

    public SpanEachRow<T1, T2, T3> Current => new SpanEachRow<T1, T2, T3>(ref _r0, ref _r1, ref _r2, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4> GetEnumerator() => this;

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

    public SpanEachRow<T1, T2, T3, T4> Current => new SpanEachRow<T1, T2, T3, T4>(ref _r0, ref _r1, ref _r2, ref _r3, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5> GetEnumerator() => this;

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

    public SpanEachRow<T1, T2, T3, T4, T5> Current => new SpanEachRow<T1, T2, T3, T4, T5>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    public SpanEachRow<T1, T2, T3, T4, T5, T6> Current => new SpanEachRow<T1, T2, T3, T4, T5, T6>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, ref _r5, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public ref T6 Get5() => ref _r5;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7> GetEnumerator() => this;

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

    public SpanEachRow<T1, T2, T3, T4, T5, T6, T7> Current => new SpanEachRow<T1, T2, T3, T4, T5, T6, T7>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, ref _r5, ref _r6, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public ref T6 Get5() => ref _r5;
    public ref T7 Get6() => ref _r6;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpanEach<T1, T2, T3, T4, T5, T6, T7, T8> GetEnumerator() => this;

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

    public SpanEachRow<T1, T2, T3, T4, T5, T6, T7, T8> Current => new SpanEachRow<T1, T2, T3, T4, T5, T6, T7, T8>(ref _r0, ref _r1, ref _r2, ref _r3, ref _r4, ref _r5, ref _r6, ref _r7, _entities[_rowCount - 1 - _remaining]);

    public ref T1 Get0() => ref _r0;
    public ref T2 Get1() => ref _r1;
    public ref T3 Get2() => ref _r2;
    public ref T4 Get3() => ref _r3;
    public ref T5 Get4() => ref _r4;
    public ref T6 Get5() => ref _r5;
    public ref T7 Get6() => ref _r6;
    public ref T8 Get7() => ref _r7;
    public Entity Entity => _entities[_rowCount - 1 - _remaining];
}
