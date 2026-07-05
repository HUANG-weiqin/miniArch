using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Single-threaded <see cref="CommandStreamCore"/>. All mutators run without
/// synchronization; callers must ensure no concurrent access from multiple threads.
/// <para/>
/// For multi-threaded recording, use <see cref="ParallelCommandStream"/>.
/// </summary>
/// <remarks>
/// <b>Why a dedicated single-threaded type:</b> removing the per-call mode check
/// that a combined type would need (<c>if (parallel) lock(...); ...</c>) eliminates
/// a branch the CPU predictor must track on every recording API. On the
/// single-threaded hot path (record+submit per frame) this is the difference
/// between a direct call and a guarded dispatch.
/// </remarks>
public sealed class CommandStream : CommandStreamCore
{
    /// <summary>
    /// Creates a new single-threaded command stream bound to <paramref name="world"/>.
    /// </summary>
    public CommandStream(World world) : base(world) { }

    /// <summary>
    /// Records a deferred entity creation and returns the new entity (placeholder or real).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create() => CreateCore();

    /// <summary>
    /// Creates a tracked handle for <paramref name="entity"/> that auto-updates
    /// when a deferred placeholder is resolved during Submit or Replay.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntitySlot Track(Entity entity) => TrackCore(entity);

    /// <summary>
    /// Records an Add command for the specified component on the given entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            WritePendingComponent(batchIdx, component);
        else
            GetOrCreateStore<T>().Append(entity, component, KindAdd);
    }

    /// <summary>
    /// Records a Set command for the specified component on the given entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            WritePendingComponent(batchIdx, component);
        else
            GetOrCreateStore<T>().Append(entity, component, KindSet);
    }

    /// <summary>
    /// Records a Remove command for the specified component type from the given entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        else
            GetOrCreateStore<T>().AppendRemove(entity);
    }

    /// <summary>
    /// Records a Destroy command for the specified entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Destroy(Entity entity) => DestroyCore(entity);

    /// <summary>
    /// Records an AddChild command establishing a parent-child relationship.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddChild(Entity parent, Entity child) => AddChildCore(parent, child);

    /// <summary>
    /// Records a RemoveChild command detaching the entity from its parent.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveChild(Entity child) => RemoveChildCore(child);

    /// <summary>
    /// Records a clone of the source entity, including all components and descendants.
    /// </summary>
    public Entity Clone(Entity source) => CloneCore(source);
}
