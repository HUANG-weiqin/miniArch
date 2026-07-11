using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Writer callback used by <c>CreateMany</c> to compute the single component
/// value for each entity in a batch-create operation.
/// </summary>
/// <typeparam name="T1">The component type to write for each entity.</typeparam>
public interface ICreateManyWriter<T1> where T1 : unmanaged
{
    /// <summary>
    /// Computes the component value for the entity at <paramref name="index"/>.
    /// </summary>
    /// <param name="index">Zero-based index of the entity within the batch span.</param>
    /// <param name="entity">The just-reserved entity handle (not yet alive until Submit/Snapshot/Replay).</param>
    /// <param name="component1">Output: the component value to attach to <paramref name="entity"/>.</param>
    void Write(int index, Entity entity, out T1 component1);
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
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = _lastCreatedBatch;
            writer.Write(i, entity, out T1 c1);
            WritePendingComponent(batchIdx, c1);
        }
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
}
