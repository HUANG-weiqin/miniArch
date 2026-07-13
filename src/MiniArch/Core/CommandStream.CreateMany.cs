using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Writer callback used by <c>CreateMany</c> to compute the single component
/// value for each entity in a batch-create operation.
/// </summary>
/// <remarks>
/// <b>Constraint:</b> The <see cref="Write"/> implementation <b>must not</b>
/// call back into the same <see cref="CommandStream"/> / <see cref="ParallelCommandStream"/>
/// (no <c>Create</c>, <c>Destroy</c>, <c>Add</c>, <c>Set</c>, <c>Remove</c>,
/// <c>Submit</c>, <c>Snapshot</c>, or <c>Clear</c>). Doing so corrupts the
/// recording state and produces undefined behavior.
/// This constraint applies to all <c>ICreateManyWriter</c> overloads.
/// </remarks>
/// <typeparam name="T1">The component type to write for each entity.</typeparam>
public interface ICreateManyWriter<T1> where T1 : unmanaged
{
    /// <summary>
    /// Computes the component value for the entity at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">Zero-based index of the entity within the batch span.</param>
    /// <param name="entity">The just-reserved entity handle (not yet alive until Submit/Snapshot/Replay).</param>
    /// <param name="c1">Output: the component value to attach to <paramref name="entity"/>.</param>
    void Write(int index, Entity entity, out T1 c1);
}

/// <summary>
/// Writer callback for two-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2);
}

/// <summary>
/// Writer callback for three-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2, out T3 c3);
}

/// <summary>
/// Writer callback for four-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2, T3, T4>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4);
}

/// <summary>
/// Writer callback for five-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2, T3, T4, T5>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged
    where T5 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5);
}

/// <summary>
/// Writer callback for six-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2, T3, T4, T5, T6>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged
    where T5 : unmanaged
    where T6 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5, out T6 c6);
}

/// <summary>
/// Writer callback for seven-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged
    where T5 : unmanaged
    where T6 : unmanaged
    where T7 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5, out T6 c6, out T7 c7);
}

/// <summary>
/// Writer callback for eight-component <c>CreateMany</c> batches.
/// </summary>
public interface ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7, T8>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged
    where T5 : unmanaged
    where T6 : unmanaged
    where T7 : unmanaged
    where T8 : unmanaged
{
    void Write(int index, Entity entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5, out T6 c6, out T7 c7, out T8 c8);
}

public abstract partial class CommandStreamCore
{
    /// <summary>
    /// Core batch-create logic for single-component entities. Reserves each
    /// entity via <see cref="CreateCore"/>, invokes <paramref name="writer"/>
    /// to compute its component, and records the component into the pending
    /// batch buffer via <see cref="WritePendingComponent{T}"/>. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1);
            WritePendingComponent(batchIdx, c1);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 1,
            t1, default, default, default, default, default, default, default);
    }

    /// <summary>
    /// Core batch-create logic for two-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 2,
            t1, t2, default, default, default, default, default, default);
    }

    /// <summary>
    /// Core batch-create logic for three-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, T3, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        var t3 = CommandTypeInfo<T3>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2, out T3 c3);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
            WritePendingComponent(batchIdx, c3);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 3,
            t1, t2, t3, default, default, default, default, default);
    }

    /// <summary>
    /// Core batch-create logic for four-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, T3, T4, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        var t3 = CommandTypeInfo<T3>.Type;
        var t4 = CommandTypeInfo<T4>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
            WritePendingComponent(batchIdx, c3);
            WritePendingComponent(batchIdx, c4);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 4,
            t1, t2, t3, t4, default, default, default, default);
    }

    /// <summary>
    /// Core batch-create logic for five-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, T3, T4, T5, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        var t3 = CommandTypeInfo<T3>.Type;
        var t4 = CommandTypeInfo<T4>.Type;
        var t5 = CommandTypeInfo<T5>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
            WritePendingComponent(batchIdx, c3);
            WritePendingComponent(batchIdx, c4);
            WritePendingComponent(batchIdx, c5);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 5,
            t1, t2, t3, t4, t5, default, default, default);
    }

    /// <summary>
    /// Core batch-create logic for six-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, T3, T4, T5, T6, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        var t3 = CommandTypeInfo<T3>.Type;
        var t4 = CommandTypeInfo<T4>.Type;
        var t5 = CommandTypeInfo<T5>.Type;
        var t6 = CommandTypeInfo<T6>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5, out T6 c6);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
            WritePendingComponent(batchIdx, c3);
            WritePendingComponent(batchIdx, c4);
            WritePendingComponent(batchIdx, c5);
            WritePendingComponent(batchIdx, c6);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 6,
            t1, t2, t3, t4, t5, t6, default, default);
    }

    /// <summary>
    /// Core batch-create logic for seven-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, T3, T4, T5, T6, T7, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where T7 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        var t3 = CommandTypeInfo<T3>.Type;
        var t4 = CommandTypeInfo<T4>.Type;
        var t5 = CommandTypeInfo<T5>.Type;
        var t6 = CommandTypeInfo<T6>.Type;
        var t7 = CommandTypeInfo<T7>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5, out T6 c6, out T7 c7);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
            WritePendingComponent(batchIdx, c3);
            WritePendingComponent(batchIdx, c4);
            WritePendingComponent(batchIdx, c5);
            WritePendingComponent(batchIdx, c6);
            WritePendingComponent(batchIdx, c7);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 7,
            t1, t2, t3, t4, t5, t6, t7, default);
    }

    /// <summary>
    /// Core batch-create logic for eight-component entities. Shared by both
    /// subclasses; callers handle synchronization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, T2, T3, T4, T5, T6, T7, T8, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where T7 : unmanaged
        where T8 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        if (entities.Length == 0) return;
        var startBatch = _frozen.PendingBatchCount;
        var t1 = CommandTypeInfo<T1>.Type;
        var t2 = CommandTypeInfo<T2>.Type;
        var t3 = CommandTypeInfo<T3>.Type;
        var t4 = CommandTypeInfo<T4>.Type;
        var t5 = CommandTypeInfo<T5>.Type;
        var t6 = CommandTypeInfo<T6>.Type;
        var t7 = CommandTypeInfo<T7>.Type;
        var t8 = CommandTypeInfo<T8>.Type;
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1, out T2 c2, out T3 c3, out T4 c4, out T5 c5, out T6 c6, out T7 c7, out T8 c8);
            WritePendingComponent(batchIdx, c1);
            WritePendingComponent(batchIdx, c2);
            WritePendingComponent(batchIdx, c3);
            WritePendingComponent(batchIdx, c4);
            WritePendingComponent(batchIdx, c5);
            WritePendingComponent(batchIdx, c6);
            WritePendingComponent(batchIdx, c7);
            WritePendingComponent(batchIdx, c8);
        }
        AppendCreateManyGroup(startBatch, entities.Length, 8,
            t1, t2, t3, t4, t5, t6, t7, t8);
    }
}

public sealed partial class CommandStream
{
    /// <summary>
    /// Batch-creates <c>entities.Length</c> entities, each with a single
    /// component of type <typeparamref name="T1"/> computed by
    /// <paramref name="writer"/>. The reserved handles are written back into
    /// <paramref name="entities"/>. Equivalent to calling
    /// <see cref="Create"/> + <see cref="Add{T}"/> per entity, but reads the
    /// batch index directly instead of re-looking it up.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1>
        => CreateManyCore<T1, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with two components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2>
        => CreateManyCore<T1, T2, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with three components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, T3, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3>
        => CreateManyCore<T1, T2, T3, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with four components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, T3, T4, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4>
        => CreateManyCore<T1, T2, T3, T4, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with five components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, T3, T4, T5, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5>
        => CreateManyCore<T1, T2, T3, T4, T5, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with six components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, T3, T4, T5, T6, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6>
        => CreateManyCore<T1, T2, T3, T4, T5, T6, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with seven components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, T3, T4, T5, T6, T7, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where T7 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7>
        => CreateManyCore<T1, T2, T3, T4, T5, T6, T7, TWriter>(entities, writer);

    /// <summary>
    /// Batch-creates entities, each with eight components computed by
    /// <paramref name="writer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, T2, T3, T4, T5, T6, T7, T8, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where T7 : unmanaged
        where T8 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7, T8>
        => CreateManyCore<T1, T2, T3, T4, T5, T6, T7, T8, TWriter>(entities, writer);
}

public sealed partial class ParallelCommandStream
{
    /// <summary>
    /// Batch-creates <c>entities.Length</c> entities, each with a single
    /// component of type <typeparamref name="T1"/> computed by
    /// <paramref name="writer"/>. Thread-safe; serializes on the internal
    /// create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with two components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with three components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, T3, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, T3, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with four components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, T3, T4, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, T3, T4, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with five components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, T3, T4, T5, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, T3, T4, T5, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with six components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, T3, T4, T5, T6, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, T3, T4, T5, T6, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with seven components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, T3, T4, T5, T6, T7, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where T7 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, T3, T4, T5, T6, T7, TWriter>(entities, writer);
    }

    /// <summary>
    /// Batch-creates entities, each with eight components. Thread-safe;
    /// serializes on the internal create lock around the entire batch.
    /// </summary>
    public void CreateMany<T1, T2, T3, T4, T5, T6, T7, T8, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged
        where T5 : unmanaged
        where T6 : unmanaged
        where T7 : unmanaged
        where T8 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, T2, T3, T4, T5, T6, T7, T8, TWriter>(entities, writer);
    }
}
