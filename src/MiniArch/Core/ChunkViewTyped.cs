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
    private readonly ComponentType _ct0;
    internal ChunkViewEnumerable(Query query) { _query = query; _ct0 = Component<T1>.ComponentType; }
    /// <summary>Gets the chunk enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ChunkViewEnumerator<T1> GetEnumerator() => new(_query, _ct0);
}

/// <summary>
/// Enumerates chunks matched by a query, yielding <see cref="ChunkView{T1}"/>
/// for each non-empty chunk.
/// </summary>
public ref struct ChunkViewEnumerator<T1> where T1 : struct
{
    private ReadOnlySpan<Archetype> _archetypes;
    private ReadOnlySpan<Chunk> _currentChunks;
    private int _archetypeIdx, _chunkIdx;
    private ComponentType _ct0;
    private int _col0;

    internal ChunkViewEnumerator(Query query, ComponentType ct0)
    {
        _archetypes = query.GetArchetypeSpan();
        _currentChunks = default;
        _archetypeIdx = -1; _chunkIdx = -1;
        _ct0 = ct0; _col0 = -1;
        Current = default;
    }

    /// <summary>Gets the current chunk view.</summary>
    public ChunkView<T1> Current { get; private set; }
    /// <summary>Advances to the next matched chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx < _currentChunks.Length)
            {
                Current = new ChunkView<T1>(_currentChunks[_chunkIdx], _col0);
                return true;
            }
            _archetypeIdx++;
            if (_archetypeIdx >= _archetypes.Length) return false;
            _currentChunks = _archetypes[_archetypeIdx].GetChunkSpan();
            _col0 = _archetypes[_archetypeIdx].GetComponentIndexFast(_ct0);
            _chunkIdx = -1;
        }
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
    private readonly ComponentType _ct0, _ct1;
    internal ChunkViewEnumerable(Query query) { _query = query; _ct0 = Component<T1>.ComponentType; _ct1 = Component<T2>.ComponentType; }
    /// <summary>Gets the chunk enumerator.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ChunkViewEnumerator<T1, T2> GetEnumerator() => new(_query, _ct0, _ct1);
}

/// <summary>Enumerator yielding <see cref="ChunkView{T1,T2}"/> per chunk.</summary>
public ref struct ChunkViewEnumerator<T1, T2> where T1 : struct where T2 : struct
{
    private ReadOnlySpan<Archetype> _archetypes;
    private ReadOnlySpan<Chunk> _currentChunks;
    private int _archetypeIdx, _chunkIdx;
    private ComponentType _ct0, _ct1;
    private int _col0, _col1;
    internal ChunkViewEnumerator(Query query, ComponentType ct0, ComponentType ct1)
    { _archetypes = query.GetArchetypeSpan(); _currentChunks = default; _archetypeIdx = -1; _chunkIdx = -1; _ct0 = ct0; _ct1 = ct1; _col0 = -1; _col1 = -1; Current = default; }
    /// <summary>Gets the current chunk view.</summary>
    public ChunkView<T1, T2> Current { get; private set; }
    /// <summary>Advances to the next matched chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (true)
        {
            _chunkIdx++;
            if (_chunkIdx < _currentChunks.Length) { Current = new ChunkView<T1, T2>(_currentChunks[_chunkIdx], _col0, _col1); return true; }
            _archetypeIdx++;
            if (_archetypeIdx >= _archetypes.Length) return false;
            _currentChunks = _archetypes[_archetypeIdx].GetChunkSpan();
            _col0 = _archetypes[_archetypeIdx].GetComponentIndexFast(_ct0);
            _col1 = _archetypes[_archetypeIdx].GetComponentIndexFast(_ct1);
            _chunkIdx = -1;
        }
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
    /// <summary>Number of entities in the chunk.</summary>
    public int Count{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.Count;}
    /// <summary>Span of <typeparamref name="T1"/> component data.</summary>
    public Span<T1> Span0{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.GetComponentSpanAt<T1>(_c0);}
    /// <summary>Span of <typeparamref name="T2"/> component data.</summary>
    public Span<T2> Span1{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.GetComponentSpanAt<T2>(_c1);}
    /// <summary>Span of <typeparamref name="T3"/> component data.</summary>
    public Span<T3> Span2{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_chunk.GetComponentSpanAt<T3>(_c2);}
    /// <summary>Entities stored in the chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]public ReadOnlySpan<Entity> GetEntities()=>_chunk.GetEntities();
}

/// <summary>Enumerable for <see cref="ChunkViewEnumerator{T1,T2,T3}"/>.</summary>
public readonly struct ChunkViewEnumerable<T1,T2,T3> where T1:struct where T2:struct where T3:struct
{ readonly Query _q; readonly ComponentType _c0,_c1,_c2; internal ChunkViewEnumerable(Query q){_q=q;_c0=Component<T1>.ComponentType;_c1=Component<T2>.ComponentType;_c2=Component<T3>.ComponentType;}
  /// <summary>Gets the chunk enumerator.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public ChunkViewEnumerator<T1,T2,T3> GetEnumerator()=>new(_q,_c0,_c1,_c2);}

/// <summary>Enumerator yielding <see cref="ChunkView{T1,T2,T3}"/> per chunk.</summary>
public ref struct ChunkViewEnumerator<T1,T2,T3> where T1:struct where T2:struct where T3:struct
{ ReadOnlySpan<Archetype> _at; ReadOnlySpan<Chunk> _cs; int _ai,_ci; ComponentType _t0,_t1,_t2; int _c0,_c1,_c2;
  internal ChunkViewEnumerator(Query q,ComponentType t0,ComponentType t1,ComponentType t2){_at=q.GetArchetypeSpan();_cs=default;_ai=-1;_ci=-1;_t0=t0;_t1=t1;_t2=t2;_c0=-1;_c1=-1;_c2=-1;Current=default;}
  /// <summary>Gets the current chunk view.</summary>
  public ChunkView<T1,T2,T3> Current{get;private set;}
  /// <summary>Advances to the next matched chunk.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public bool MoveNext(){while(true){_ci++;if(_ci<_cs.Length){Current=new(_cs[_ci],_c0,_c1,_c2);return true;}_ai++;if(_ai>=_at.Length)return false;_cs=_at[_ai].GetChunkSpan();_c0=_at[_ai].GetComponentIndexFast(_t0);_c1=_at[_ai].GetComponentIndexFast(_t1);_c2=_at[_ai].GetComponentIndexFast(_t2);_ci=-1;}}
}

// ============================================================================
// ChunkView<T1,T2,T3,T4> — four components
// ============================================================================

/// <summary>Typed view of a chunk with four component types.</summary>
public readonly ref struct ChunkView<T1,T2,T3,T4> where T1:struct where T2:struct where T3:struct where T4:struct
{ readonly Chunk _c; readonly int _i0,_i1,_i2,_i3;
  [MethodImpl(MethodImplOptions.AggressiveInlining)]internal ChunkView(Chunk c,int i0,int i1,int i2,int i3){_c=c;_i0=i0;_i1=i1;_i2=i2;_i3=i3;}
  /// <summary>Number of entities in the chunk.</summary>
  public int Count{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.Count;}
  /// <summary>Span of <typeparamref name="T1"/> component data.</summary>
  public Span<T1> Span0{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T1>(_i0);}
  /// <summary>Span of <typeparamref name="T2"/> component data.</summary>
  public Span<T2> Span1{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T2>(_i1);}
  /// <summary>Span of <typeparamref name="T3"/> component data.</summary>
  public Span<T3> Span2{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T3>(_i2);}
  /// <summary>Span of <typeparamref name="T4"/> component data.</summary>
  public Span<T4> Span3{[MethodImpl(MethodImplOptions.AggressiveInlining)]get=>_c.GetComponentSpanAt<T4>(_i3);}
  /// <summary>Entities stored in the chunk.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public ReadOnlySpan<Entity> GetEntities()=>_c.GetEntities();}

/// <summary>Enumerable for <see cref="ChunkViewEnumerator{T1,T2,T3,T4}"/>.</summary>
public readonly struct ChunkViewEnumerable<T1,T2,T3,T4> where T1:struct where T2:struct where T3:struct where T4:struct
{ readonly Query _q; readonly ComponentType _t0,_t1,_t2,_t3; internal ChunkViewEnumerable(Query q){_q=q;_t0=Component<T1>.ComponentType;_t1=Component<T2>.ComponentType;_t2=Component<T3>.ComponentType;_t3=Component<T4>.ComponentType;}
  /// <summary>Gets the chunk enumerator.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public ChunkViewEnumerator<T1,T2,T3,T4> GetEnumerator()=>new(_q,_t0,_t1,_t2,_t3);}

/// <summary>Enumerator yielding <see cref="ChunkView{T1,T2,T3,T4}"/> per chunk.</summary>
public ref struct ChunkViewEnumerator<T1,T2,T3,T4> where T1:struct where T2:struct where T3:struct where T4:struct
{ ReadOnlySpan<Archetype> _at; ReadOnlySpan<Chunk> _cs; int _ai,_ci; ComponentType _t0,_t1,_t2,_t3; int _i0,_i1,_i2,_i3;
  internal ChunkViewEnumerator(Query q,ComponentType t0,ComponentType t1,ComponentType t2,ComponentType t3){_at=q.GetArchetypeSpan();_cs=default;_ai=-1;_ci=-1;_t0=t0;_t1=t1;_t2=t2;_t3=t3;_i0=-1;_i1=-1;_i2=-1;_i3=-1;Current=default;}
  /// <summary>Gets the current chunk view.</summary>
  public ChunkView<T1,T2,T3,T4> Current{get;private set;}
  /// <summary>Advances to the next matched chunk.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]public bool MoveNext(){while(true){_ci++;if(_ci<_cs.Length){Current=new(_cs[_ci],_i0,_i1,_i2,_i3);return true;}_ai++;if(_ai>=_at.Length)return false;_cs=_at[_ai].GetChunkSpan();_i0=_at[_ai].GetComponentIndexFast(_t0);_i1=_at[_ai].GetComponentIndexFast(_t1);_i2=_at[_ai].GetComponentIndexFast(_t2);_i3=_at[_ai].GetComponentIndexFast(_t3);_ci=-1;}}
}
