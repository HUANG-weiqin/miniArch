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
    /// <para/>
    /// <b>Pending entity note:</b> If <paramref name="entity"/> is a pending
    /// (Create'd but not yet Submit'd/Snapshot'd) entity, this Add is recorded
    /// in the batch buffer and folded with any other Add/Set/Remove on the same
    /// entity into the final materialized component signature. Intermediate
    /// operations are <b>not</b> observable via <c>World.Watch</c> handles
    /// (the snapshot/diff lifecycle is orthogonal to pending batching) — only the net final state is materialized.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else if (!entity.IsPlaceholder && _world.IsAlive(entity))
        {
            GetOrCreateStore<T>().Append(entity, component, KindAdd);
        }
    }

    /// <summary>
    /// Records a Set command for the specified component on the given entity.
    /// <para/>
    /// <b>Pending entity note:</b> Same folding semantics as <see cref="Add{T}"/>.
    /// For pending entities, multiple Set invocations are collapsed to the last
    /// value during materialization; no intermediate <c>ChangeWatch&lt;,&gt;.Diff</c>
    /// entries are produced.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else if (!entity.IsPlaceholder && _world.IsAlive(entity))
        {
            GetOrCreateStore<T>().Append(entity, component, KindSet);
        }
    }

    /// <summary>
    /// Records a Remove command for the specified component type from the given entity.
    /// <para/>
    /// <b>Pending entity note:</b> Same folding semantics as <see cref="Add{T}"/>.
    /// For pending entities, Remove is recorded in the batch buffer. If the same
    /// entity was also Add'd the same type, the net effect during materialization
    /// may eliminate the type entirely; no intermediate <c>TransitionWatch&lt;&gt;.Diff</c>
    /// (Entered followed by Exited) entries are observable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        }
        else if (!entity.IsPlaceholder && _world.IsAlive(entity))
        {
            GetOrCreateStore<T>().AppendRemove(entity);
        }
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
