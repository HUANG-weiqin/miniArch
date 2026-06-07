using System.Runtime.CompilerServices;

namespace MiniArch.Core;

// ============================================================================
// ChunkView<T1> — single component
// ============================================================================

/// <summary>
/// Typed view of a single chunk's component storage for one component type.
/// Returned by <see cref="ChunkViewEnumerator{T1}"/>; use in a foreach loop
/// via <c>Query.Chunks&lt;T1&gt;</c>.
/// </summary>
public readonly ref struct ChunkView<T1> where T1 : struct
{
    private readonly Chunk _chunk;
    private readonly int _col0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkView(Chunk chunk, int col0) { _chunk = chunk; _col0 = col0; }

    /// <summary>Number of entities in the chunk.</summary>
    public int Count { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _chunk.Count; }
    /// <summary>Span of <typeparamref name="T1"/> component data in the chunk.</summary>
    public Span<T1> Span0 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _chunk.GetComponentSpanAt<T1>(_col0); }
    /// <summary>Entities stored in the chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ReadOnlySpan<Entity> GetEntities() => _chunk.GetEntities();
}

/// <summary>
/// Enumerable providing <see cref="ChunkViewEnumerator{T1}"/> to iterate
/// over chunks matched by a query.
/// </summary>
public readonly struct ChunkViewEnumerable<T1> where T1 : struct
{
    private readonly Query _query;
    internal ChunkViewEnumerable(Query query) { _query = query; }
    /// <summary>Gets the chunk enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ChunkViewEnumerator<T1> GetEnumerator() => new(_query);
}

/// <summary>
/// Enumerates chunks matched by a query, yielding <see cref="ChunkView{T1}"/>
/// for each non-empty chunk. Iterates directly over the query's chunk snapshot.
/// </summary>
public ref struct ChunkViewEnumerator<T1> where T1 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;

    internal ChunkViewEnumerator(Query query)
    {
        _chunks = query.GetChunkSpan();
        _chunkIdx = -1;
        Current = default;
    }

    /// <summary>Gets the current chunk view.</summary>
    public ChunkView<T1> Current { get; private set; }

    /// <summary>Advances to the next matched chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _chunkIdx++;
        if (_chunkIdx >= _chunks.Length) return false;
        var c = _chunks[_chunkIdx];
        Current = new ChunkView<T1>(c, c.GetComponentIndexFast(Component<T1>.ComponentType));
        return true;
    }
}

// ============================================================================
// ChunkView<T1,T2> — two components
// ============================================================================

/// <summary>Typed view of a chunk with two component types.</summary>
public readonly ref struct ChunkView<T1, T2> where T1 : struct where T2 : struct
{
    private readonly Chunk _chunk;
    private readonly int _col0, _col1;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkView(Chunk chunk, int col0, int col1) { _chunk = chunk; _col0 = col0; _col1 = col1; }
    /// <summary>Number of entities in the chunk.</summary>
    public int Count { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _chunk.Count; }
    /// <summary>Span of <typeparamref name="T1"/> component data.</summary>
    public Span<T1> Span0 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _chunk.GetComponentSpanAt<T1>(_col0); }
    /// <summary>Span of <typeparamref name="T2"/> component data.</summary>
    public Span<T2> Span1 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _chunk.GetComponentSpanAt<T2>(_col1); }
    /// <summary>Entities stored in the chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ReadOnlySpan<Entity> GetEntities() => _chunk.GetEntities();
}

/// <summary>Enumerable for <see cref="ChunkViewEnumerator{T1,T2}"/>.</summary>
public readonly struct ChunkViewEnumerable<T1, T2> where T1 : struct where T2 : struct
{
    private readonly Query _query;
    internal ChunkViewEnumerable(Query query) { _query = query; }
    /// <summary>Gets the chunk enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ChunkViewEnumerator<T1, T2> GetEnumerator() => new(_query);
}

/// <summary>Enumerator yielding <see cref="ChunkView{T1,T2}"/> per chunk.</summary>
public ref struct ChunkViewEnumerator<T1, T2> where T1 : struct where T2 : struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;

    internal ChunkViewEnumerator(Query query)
    {
        _chunks = query.GetChunkSpan();
        _chunkIdx = -1;
        Current = default;
    }

    /// <summary>Gets the current chunk view.</summary>
    public ChunkView<T1, T2> Current { get; private set; }

    /// <summary>Advances to the next matched chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _chunkIdx++;
        if (_chunkIdx >= _chunks.Length) return false;
        var c = _chunks[_chunkIdx];
        Current = new ChunkView<T1, T2>(c,
            c.GetComponentIndexFast(Component<T1>.ComponentType),
            c.GetComponentIndexFast(Component<T2>.ComponentType));
        return true;
    }
}

// ============================================================================
// ChunkView<T1,T2,T3> — three components
// ============================================================================

/// <summary>Typed view of a chunk with three component types.</summary>
public readonly ref struct ChunkView<T1, T2, T3> where T1 : struct where T2 : struct where T3 : struct
{
    private readonly Chunk _chunk; private readonly int _c0, _c1, _c2;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] internal ChunkView(Chunk c, int c0,int c1,int c2){_chunk=c;_c0=c0;_c1=c1;_c2=c2;}
    public int Count{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.Count;}
    public Span<T1> Span0{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.GetComponentSpanAt<T1>(_c0);}
    public Span<T2> Span1{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.GetComponentSpanAt<T2>(_c1);}
    public Span<T3> Span2{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.GetComponentSpanAt<T3>(_c2);}
    [MethodImpl(MethodImplOptions.AggressiveInlining)]public ReadOnlySpan<Entity> GetEntities()=>_chunk.GetEntities();
}

/// <summary>Enumerable for <see cref="ChunkViewEnumerator{T1,T2,T3}"/>.</summary>
public readonly struct ChunkViewEnumerable<T1,T2,T3> where T1:struct where T2:struct where T3:struct
{ readonly Query _q; internal ChunkViewEnumerable(Query q){_q=q;}
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public ChunkViewEnumerator<T1,T2,T3> GetEnumerator()=>new(_q);}

/// <summary>Enumerator yielding <see cref="ChunkView{T1,T2,T3}"/> per chunk.</summary>
public ref struct ChunkViewEnumerator<T1,T2,T3> where T1:struct where T2:struct where T3:struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;

    internal ChunkViewEnumerator(Query q)
    {
        _chunks = q.GetChunkSpan();
        _chunkIdx = -1;
        Current = default;
    }

    public ChunkView<T1,T2,T3> Current{get;private set;}

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _chunkIdx++;
        if (_chunkIdx >= _chunks.Length) return false;
        var c = _chunks[_chunkIdx];
        Current = new ChunkView<T1, T2, T3>(c,
            c.GetComponentIndexFast(Component<T1>.ComponentType),
            c.GetComponentIndexFast(Component<T2>.ComponentType),
            c.GetComponentIndexFast(Component<T3>.ComponentType));
        return true;
    }
}

// ============================================================================
// ChunkView<T1,T2,T3,T4> — four components
// ============================================================================

/// <summary>Typed view of a chunk with four component types.</summary>
public readonly ref struct ChunkView<T1,T2,T3,T4> where T1:struct where T2:struct where T3:struct where T4:struct
{ readonly Chunk _c; readonly int _i0,_i1,_i2,_i3;
  [MethodImpl(MethodImplOptions.AggressiveInlining)]internal ChunkView(Chunk c,int i0,int i1,int i2,int i3){_c=c;_i0=i0;_i1=i1;_i2=i2;_i3=i3;}
  public int Count{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.Count;}
  public Span<T1> Span0{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T1>(_i0);}
  public Span<T2> Span1{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T2>(_i1);}
  public Span<T3> Span2{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T3>(_i2);}
  public Span<T4> Span3{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T4>(_i3);}
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public ReadOnlySpan<Entity> GetEntities()=>_c.GetEntities();}

/// <summary>Enumerable for <see cref="ChunkViewEnumerator{T1,T2,T3,T4}"/>.</summary>
public readonly struct ChunkViewEnumerable<T1,T2,T3,T4> where T1:struct where T2:struct where T3:struct where T4:struct
{ readonly Query _q; internal ChunkViewEnumerable(Query q){_q=q;}
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public ChunkViewEnumerator<T1,T2,T3,T4> GetEnumerator()=>new(_q);}

/// <summary>Enumerator yielding <see cref="ChunkView{T1,T2,T3,T4}"/> per chunk.</summary>
public ref struct ChunkViewEnumerator<T1,T2,T3,T4> where T1:struct where T2:struct where T3:struct where T4:struct
{
    private ReadOnlySpan<Chunk> _chunks;
    private int _chunkIdx;

    internal ChunkViewEnumerator(Query q)
    {
        _chunks = q.GetChunkSpan();
        _chunkIdx = -1;
        Current = default;
    }

    public ChunkView<T1,T2,T3,T4> Current{get;private set;}

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        _chunkIdx++;
        if (_chunkIdx >= _chunks.Length) return false;
        var c = _chunks[_chunkIdx];
        Current = new ChunkView<T1, T2, T3, T4>(c,
            c.GetComponentIndexFast(Component<T1>.ComponentType),
            c.GetComponentIndexFast(Component<T2>.ComponentType),
            c.GetComponentIndexFast(Component<T3>.ComponentType),
            c.GetComponentIndexFast(Component<T4>.ComponentType));
        return true;
    }
}
