// Component models, core data types, operator interfaces, and Rows DSL
// for FrameReadModel ValueLab experiments.
// Not part of the public MiniArch API — local to this lab only.

using MiniArch;

namespace FrameReadModels.ValueLab;

// ========================================================================
//  Component models (all unmanaged value types)
// ========================================================================

/// <summary>2D position component (int coordinates for deterministic tests).</summary>
internal readonly record struct Position(int X, int Y);

/// <summary>2D velocity component.</summary>
internal readonly record struct Velocity(int Dx, int Dy);

/// <summary>Health value component.</summary>
internal readonly record struct Health(int Value);

/// <summary>Team affiliation component — used as a key in grouping queries.</summary>
internal readonly record struct Team(int Value);

/// <summary>Cell identifier component — used as a bounded int key.</summary>
internal readonly record struct Cell(int Value);

/// <summary>
/// Key with a constant hash code (always 42).
/// Used to force all keys into a single hash bucket, testing hash collision
/// handling in open-addressed tables.
/// </summary>
internal readonly struct CollisionKey : IEquatable<CollisionKey>
{
    public readonly int Value;

    public CollisionKey(int value) { Value = value; }

    public bool Equals(CollisionKey other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is CollisionKey other && Equals(other);
    public override int GetHashCode() => 42; // constant — all in one bucket
    public override string ToString() => $"CollisionKey({Value})";
}

// ========================================================================
//  Row-level position in chunk storage
// ========================================================================

/// <summary>
/// Identifies a single entity row within the chunk array produced by
/// <c>world.Query(desc).GetChunks()</c>.
/// ChunkIndex is the index into the ChunkView[] returned by GetChunks();
/// RowIndex is the position within that chunk's entity/component spans.
/// A RowRef is only valid while the world's archetype structure is unchanged.
/// </summary>
internal readonly struct RowRef
{
    public readonly int ChunkIndex;
    public readonly int RowIndex;

    public RowRef(int chunkIndex, int rowIndex)
    {
        ChunkIndex = chunkIndex;
        RowIndex = rowIndex;
    }

    public ref T Component<T>(ReadOnlySpan<ChunkView> chunks) where T : unmanaged
        => ref chunks[ChunkIndex].GetSpan<T>()[RowIndex];

    public override string ToString() => $"RowRef(c{ChunkIndex}, r{RowIndex})";
}

/// <summary>
/// Describes a contiguous range of rows for a single key within the
/// flat row-ref array of a <see cref="CompactRowLookup{TKey}"/>.
/// </summary>
internal readonly struct ChunkRun
{
    /// <summary>Index into the original GetChunks() span.</summary>
    public readonly int ChunkIndex;
    /// <summary>Start position within the chunk (inclusive).</summary>
    public readonly int Start;
    /// <summary>Number of consecutive rows in this run.</summary>
    public readonly int Length;

    public ChunkRun(int chunkIndex, int start, int length)
    {
        ChunkIndex = chunkIndex;
        Start = start;
        Length = length;
    }
}

// ========================================================================
//  Build result metadata
// ========================================================================

/// <summary>
/// Summary of a completed build operation.
/// </summary>
internal readonly struct BuildResult
{
    /// <summary>Total number of entities that matched the predicate.</summary>
    public readonly int MatchedRows;
    /// <summary>Number of row references stored (may equal MatchedRows).</summary>
    public readonly int StoredRows;
    /// <summary>Number of distinct keys found.</summary>
    public readonly int DistinctKeys;
    /// <summary>Maximum number of entities sharing a single key.</summary>
    public readonly int MaxBucketSize;
    /// <summary>True if the internal storage was resized during build.</summary>
    public readonly bool Resized;

    public BuildResult(int matchedRows, int storedRows, int distinctKeys, int maxBucketSize, bool resized)
    {
        MatchedRows = matchedRows;
        StoredRows = storedRows;
        DistinctKeys = distinctKeys;
        MaxBucketSize = maxBucketSize;
        Resized = resized;
    }
}

// ========================================================================
//  Operator interfaces — constrained generic struct pattern
//  No interface fields, no boxing, no delegates on hot path.
// ========================================================================

internal interface IFramePredicate<T1>
    where T1 : unmanaged
{
    bool Match(Entity entity, in T1 c1);
}

internal interface IFramePredicate<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    bool Match(Entity entity, in T1 c1, in T2 c2);
}

internal interface IFramePredicate<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    bool Match(Entity entity, in T1 c1, in T2 c2, in T3 c3);
}

internal interface IFrameKeySelector<TKey, T1>
    where TKey : unmanaged, IEquatable<TKey>
    where T1 : unmanaged
{
    TKey Select(Entity entity, in T1 c1);
}

internal interface IFrameKeySelector<TKey, T1, T2>
    where TKey : unmanaged, IEquatable<TKey>
    where T1 : unmanaged
    where T2 : unmanaged
{
    TKey Select(Entity entity, in T1 c1, in T2 c2);
}

internal interface IFrameKeySelector<TKey, T1, T2, T3>
    where TKey : unmanaged, IEquatable<TKey>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    TKey Select(Entity entity, in T1 c1, in T2 c2, in T3 c3);
}

internal interface IFrameRowConsumer<T1>
    where T1 : unmanaged
{
    void Accept(Entity entity, in T1 c1);
}

internal interface IFrameRowConsumer<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    void Accept(Entity entity, in T1 c1, in T2 c2);
}

internal interface IFrameRowConsumer<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    void Accept(Entity entity, in T1 c1, in T2 c2, in T3 c3);
}

internal interface IFrameRunConsumer<T1>
    where T1 : unmanaged
{
    void Accept(ReadOnlySpan<Entity> entities, ReadOnlySpan<T1> c1);
}

internal interface IFrameRunConsumer<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    void Accept(ReadOnlySpan<Entity> entities, ReadOnlySpan<T1> c1, ReadOnlySpan<T2> c2);
}

internal interface IFrameRunConsumer<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    void Accept(ReadOnlySpan<Entity> entities, ReadOnlySpan<T1> c1, ReadOnlySpan<T2> c2, ReadOnlySpan<T3> c3);
}

// ========================================================================
//  Concrete operators
// ========================================================================

/// <summary>Passes all entities (100% match).</summary>
internal readonly struct PassAll<T1> : IFramePredicate<T1>
    where T1 : unmanaged
{
    public bool Match(Entity entity, in T1 c1) => true;
}

/// <summary>Passes all entities for 2-component queries.</summary>
internal readonly struct PassAll<T1, T2> : IFramePredicate<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    public bool Match(Entity entity, in T1 c1, in T2 c2) => true;
}

/// <summary>Passes all entities for 3-component queries.</summary>
internal readonly struct PassAll<T1, T2, T3> : IFramePredicate<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    public bool Match(Entity entity, in T1 c1, in T2 c2, in T3 c3) => true;
}

/// <summary>Selects entities whose Health.Value >= threshold.</summary>
internal readonly struct HealthAtLeast : IFramePredicate<Health>
{
    private readonly int _min;
    public HealthAtLeast(int min) { _min = min; }
    public bool Match(Entity entity, in Health c1) => c1.Value >= _min;
}

/// <summary>Selects entities whose Health.Value >= threshold, with Position.</summary>
internal readonly struct HealthAtLeastWithPos : IFramePredicate<Health, Position>
{
    private readonly int _min;
    public HealthAtLeastWithPos(int min) { _min = min; }
    public bool Match(Entity entity, in Health c1, in Position c2) => c1.Value >= _min;
}

/// <summary>Selects key = Cell.Value.</summary>
internal readonly struct CellKeySelector : IFrameKeySelector<int, Cell>
{
    public int Select(Entity entity, in Cell c1) => c1.Value;
}

/// <summary>Selects key = Team.Value.</summary>
internal readonly struct TeamKeySelector : IFrameKeySelector<int, Team>
{
    public int Select(Entity entity, in Team c1) => c1.Value;
}

/// <summary>Selects key = CollisionKey from Cell.</summary>
internal readonly struct CollisionCellKeySelector : IFrameKeySelector<CollisionKey, Cell>
{
    public CollisionKey Select(Entity entity, in Cell c1) => new CollisionKey(c1.Value);
}

/// <summary>Selects key = CollisionKey from Team.</summary>
internal readonly struct CollisionTeamKeySelector : IFrameKeySelector<CollisionKey, Team>
{
    public CollisionKey Select(Entity entity, in Team c1) => new CollisionKey(c1.Value);
}

/// <summary>Selects entity itself as the key (Entity.Id).</summary>
internal readonly struct EntityIdKeySelector : IFrameKeySelector<int, Position>
{
    public int Select(Entity entity, in Position c1) => entity.Id;
}

/// <summary>Selects Entity.Id from Health component.</summary>
internal readonly struct HealthIdKeySelector : IFrameKeySelector<int, Health>
{
    public int Select(Entity entity, in Health c1) => entity.Id;
}

/// <summary>Selects Health.Value as the key.</summary>
internal readonly struct HealthValueKeySelector : IFrameKeySelector<int, Health>
{
    public int Select(Entity entity, in Health c1) => c1.Value;
}

/// <summary>
/// 3-arity selector extracting Cell.Value from Cell+Position+Health components.
/// Used by the Rows DSL for 3-component queries in benchmarks.
/// </summary>
internal readonly struct CellKeySelector3 : IFrameKeySelector<int, Cell, Position, Health>
{
    public int Select(Entity entity, in Cell c1, in Position c2, in Health c3) => c1.Value;
}

/// <summary>Sums Health.Value from a single-component row consumer.</summary>
internal struct HealthSumConsumer1 : IFrameRowConsumer<Health>
{
    public long Sum;

    public void Accept(Entity entity, in Health c1)
    {
        Sum += c1.Value;
    }
}

/// <summary>Sums Health.Value from a 3-component row consumer.</summary>
internal struct HealthSumConsumer3 : IFrameRowConsumer<Cell, Position, Health>
{
    public long Sum;

    public void Accept(Entity entity, in Cell c1, in Position c2, in Health c3)
    {
        Sum += c3.Value;
    }
}

/// <summary>Sums Health.Value from single-component chunk runs.</summary>
internal struct HealthSumRunConsumer1 : IFrameRunConsumer<Health>
{
    public long Sum;
    public int RunCount;

    public void Accept(ReadOnlySpan<Entity> entities, ReadOnlySpan<Health> c1)
    {
        RunCount++;
        for (var i = 0; i < c1.Length; i++)
            Sum += c1[i].Value;
    }
}

/// <summary>Sums Health.Value from 3-component chunk runs.</summary>
internal struct HealthSumRunConsumer3 : IFrameRunConsumer<Cell, Position, Health>
{
    public long Sum;
    public int RunCount;

    public void Accept(
        ReadOnlySpan<Entity> entities,
        ReadOnlySpan<Cell> c1,
        ReadOnlySpan<Position> c2,
        ReadOnlySpan<Health> c3)
    {
        RunCount++;
        for (var i = 0; i < c3.Length; i++)
            Sum += c3[i].Value;
    }
}

// ========================================================================
//  IFrameLookup<TKey> — unified build/read contract for all layouts
// ========================================================================

/// <summary>
/// Common interface for frame-level derived lookups.
/// All layouts implement this; the Rows DSL calls Into(ref TLookup) which
/// dispatches to TryBuild / Build.
/// </summary>
internal interface IFrameLookup<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>Resets all stored data. Capacity is preserved.</summary>
    void Clear();

    /// <summary>Returns result metadata from the last build operation.</summary>
    BuildResult LastResult { get; }

    // ---- Build ----

    /// <summary>
    /// Attempts a no-grow build. Returns false and leaves the lookup
    /// in a cleared (empty) state if capacity is insufficient.
    /// </summary>
    bool TryBuild<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>;

    /// <summary>
    /// Builds with automatic capacity growth. Always succeeds.
    /// If initial capacity is insufficient, grows internally.
    /// </summary>
    void Build<T1, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where TPred : struct, IFramePredicate<T1>
        where TSel : struct, IFrameKeySelector<TKey, T1>;

    // ---- 2-arity ----

    bool TryBuild<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>;

    void Build<T1, T2, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>;

    // ---- 3-arity ----

    bool TryBuild<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>;

    void Build<T1, T2, T3, TPred, TSel>(
        ReadOnlySpan<ChunkView> chunks, ref TPred pred, ref TSel sel)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TPred : struct, IFramePredicate<T1, T2, T3>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>;

    // ---- Read (post-build) ----

    /// <summary>Number of distinct keys in the lookup.</summary>
    int KeyCount { get; }

    /// <summary>Total number of stored rows.</summary>
    int TotalRows { get; }

    /// <summary>Gets the number of rows for a specific key.</summary>
    int GetRowCount(TKey key);

    /// <summary>
    /// Copies entities for the given key into the destination span.
    /// Returns the number of entities copied.
    /// </summary>
    int CopyEntities(TKey key, Span<Entity> dest, ReadOnlySpan<ChunkView> chunks);

    /// <summary>
    /// Copies row references for the given key into the destination span.
    /// Returns the number of refs copied.
    /// </summary>
    int CopyRowRefs(TKey key, Span<RowRef> dest);
}

// ========================================================================
//  Rows DSL — arity 1 / 2 / 3 builder chain
//  Usage:  Rows<T1>.From(world, query).Where<TPred>().KeyBy<TKey,TSel>().Into(lookup)
//  Where is optional — KeyBy can be called directly on From (→ PassAll).
// ========================================================================

// ---- T1 (single component) ----

internal static class Rows<T1> where T1 : unmanaged
{
    public static RowsFrom1<T1> From(World world, QueryDescription query)
    {
        return new RowsFrom1<T1>(world, query);
    }
}

internal readonly struct RowsFrom1<T1> where T1 : unmanaged
{
    private readonly World _world;
    private readonly QueryDescription _query;

    internal RowsFrom1(World world, QueryDescription query)
    {
        _world = world;
        _query = query;
    }

    public RowsWhere1<T1, TPred> Where<TPred>(TPred predicate = default)
        where TPred : struct, IFramePredicate<T1>
    {
        return new RowsWhere1<T1, TPred>(_world, _query, predicate);
    }

    public RowsReady1<T1, TKey, PassAll<T1>, TSel> KeyBy<TKey, TSel>(TSel selector = default)
        where TKey : unmanaged, IEquatable<TKey>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        return new RowsReady1<T1, TKey, PassAll<T1>, TSel>(
            _world, _query, default(PassAll<T1>), selector);
    }
}

internal readonly struct RowsWhere1<T1, TPred>
    where T1 : unmanaged
    where TPred : struct, IFramePredicate<T1>
{
    private readonly World _world;
    private readonly QueryDescription _query;
    private readonly TPred _predicate;

    internal RowsWhere1(World world, QueryDescription query, TPred predicate)
    {
        _world = world;
        _query = query;
        _predicate = predicate;
    }

    public RowsReady1<T1, TKey, TPred, TSel> KeyBy<TKey, TSel>(TSel selector = default)
        where TKey : unmanaged, IEquatable<TKey>
        where TSel : struct, IFrameKeySelector<TKey, T1>
    {
        return new RowsReady1<T1, TKey, TPred, TSel>(_world, _query, _predicate, selector);
    }
}

internal readonly struct RowsReady1<T1, TKey, TPred, TSel>
    where T1 : unmanaged
    where TKey : unmanaged, IEquatable<TKey>
    where TPred : struct, IFramePredicate<T1>
    where TSel : struct, IFrameKeySelector<TKey, T1>
{
    private readonly World _world;
    private readonly QueryDescription _query;
    private readonly TPred _predicate;
    private readonly TSel _selector;

    internal RowsReady1(World world, QueryDescription query, TPred predicate, TSel selector)
    {
        _world = world;
        _query = query;
        _predicate = predicate;
        _selector = selector;
    }

    public void Into<TLookup>(ref TLookup lookup)
        where TLookup : struct, IFrameLookup<TKey>
    {
        var queryResult = _world.Query(_query);
        var chunks = queryResult.GetChunks();
        var p = _predicate;
        var s = _selector;

        if (!lookup.TryBuild<T1, TPred, TSel>(chunks, ref p, ref s))
            lookup.Build<T1, TPred, TSel>(chunks, ref p, ref s);
    }
}

// ---- T1, T2 (two components) ----

internal static class Rows<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    public static RowsFrom2<T1, T2> From(World world, QueryDescription query)
    {
        return new RowsFrom2<T1, T2>(world, query);
    }
}

internal readonly struct RowsFrom2<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    private readonly World _world;
    private readonly QueryDescription _query;

    internal RowsFrom2(World world, QueryDescription query)
    {
        _world = world;
        _query = query;
    }

    public RowsWhere2<T1, T2, TPred> Where<TPred>(TPred predicate = default)
        where TPred : struct, IFramePredicate<T1, T2>
    {
        return new RowsWhere2<T1, T2, TPred>(_world, _query, predicate);
    }

    public RowsReady2<T1, T2, TKey, PassAll<T1, T2>, TSel> KeyBy<TKey, TSel>(TSel selector = default)
        where TKey : unmanaged, IEquatable<TKey>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        return new RowsReady2<T1, T2, TKey, PassAll<T1, T2>, TSel>(
            _world, _query, default(PassAll<T1, T2>), selector);
    }
}

internal readonly struct RowsWhere2<T1, T2, TPred>
    where T1 : unmanaged
    where T2 : unmanaged
    where TPred : struct, IFramePredicate<T1, T2>
{
    private readonly World _world;
    private readonly QueryDescription _query;
    private readonly TPred _predicate;

    internal RowsWhere2(World world, QueryDescription query, TPred predicate)
    {
        _world = world;
        _query = query;
        _predicate = predicate;
    }

    public RowsReady2<T1, T2, TKey, TPred, TSel> KeyBy<TKey, TSel>(TSel selector = default)
        where TKey : unmanaged, IEquatable<TKey>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2>
    {
        return new RowsReady2<T1, T2, TKey, TPred, TSel>(_world, _query, _predicate, selector);
    }
}

internal readonly struct RowsReady2<T1, T2, TKey, TPred, TSel>
    where T1 : unmanaged
    where T2 : unmanaged
    where TKey : unmanaged, IEquatable<TKey>
    where TPred : struct, IFramePredicate<T1, T2>
    where TSel : struct, IFrameKeySelector<TKey, T1, T2>
{
    private readonly World _world;
    private readonly QueryDescription _query;
    private readonly TPred _predicate;
    private readonly TSel _selector;

    internal RowsReady2(World world, QueryDescription query, TPred predicate, TSel selector)
    {
        _world = world;
        _query = query;
        _predicate = predicate;
        _selector = selector;
    }

    public void Into<TLookup>(ref TLookup lookup)
        where TLookup : struct, IFrameLookup<TKey>
    {
        var queryResult = _world.Query(_query);
        var chunks = queryResult.GetChunks();
        var p = _predicate;
        var s = _selector;

        if (!lookup.TryBuild<T1, T2, TPred, TSel>(chunks, ref p, ref s))
            lookup.Build<T1, T2, TPred, TSel>(chunks, ref p, ref s);
    }
}

// ---- T1, T2, T3 (three components) ----

internal static class Rows<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    public static RowsFrom3<T1, T2, T3> From(World world, QueryDescription query)
    {
        return new RowsFrom3<T1, T2, T3>(world, query);
    }
}

internal readonly struct RowsFrom3<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    private readonly World _world;
    private readonly QueryDescription _query;

    internal RowsFrom3(World world, QueryDescription query)
    {
        _world = world;
        _query = query;
    }

    public RowsReady3<T1, T2, T3, TKey, PassAll<T1, T2, T3>, TSel> KeyBy<TKey, TSel>(TSel selector = default)
        where TKey : unmanaged, IEquatable<TKey>
        where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
    {
        return new RowsReady3<T1, T2, T3, TKey, PassAll<T1, T2, T3>, TSel>(
            _world, _query, default(PassAll<T1, T2, T3>), selector);
    }
}

internal readonly struct RowsReady3<T1, T2, T3, TKey, TPred, TSel>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where TKey : unmanaged, IEquatable<TKey>
    where TPred : struct, IFramePredicate<T1, T2, T3>
    where TSel : struct, IFrameKeySelector<TKey, T1, T2, T3>
{
    private readonly World _world;
    private readonly QueryDescription _query;
    private readonly TPred _predicate;
    private readonly TSel _selector;

    internal RowsReady3(World world, QueryDescription query, TPred predicate, TSel selector)
    {
        _world = world;
        _query = query;
        _predicate = predicate;
        _selector = selector;
    }

    public void Into<TLookup>(ref TLookup lookup)
        where TLookup : struct, IFrameLookup<TKey>
    {
        var queryResult = _world.Query(_query);
        var chunks = queryResult.GetChunks();
        var p = _predicate;
        var s = _selector;

        if (!lookup.TryBuild<T1, T2, T3, TPred, TSel>(chunks, ref p, ref s))
            lookup.Build<T1, T2, T3, TPred, TSel>(chunks, ref p, ref s);
    }
}

// FrameLookupHelper was previously defined here but all methods were dead code.
// Deleted in 2026-07-11 cleanup — no callers existed.
