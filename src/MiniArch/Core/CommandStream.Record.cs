using System.Runtime.CompilerServices;

namespace MiniArch.Core;

// Recording is single-threaded; callers must not access a stream concurrently.
public sealed partial class CommandStream
{
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
    public EntitySlot Track(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return new EntitySlot(entity);

        var slot = new EntitySlot.Slot { Entity = entity };
        var seq = entity.Version;
        RegisterTrackedSlot(slot, seq);
        return new EntitySlot(slot);
    }

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
    /// <remarks>
    /// Existing-entity liveness is evaluated when the stream is consumed. The
    /// command is silently discarded if the entity is not alive at that point.
    /// Recording does not read the entity's current world state.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else if (!entity.IsPlaceholder)
        {
            GetOrCreateStore<T>().Append(entity, component, KindAdd);
            StampRecordedFrame();
            FlagFrameIfMayContainPlaceholder(component);
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
    /// <remarks>
    /// Existing-entity liveness is evaluated when the stream is consumed. The
    /// command is silently discarded if the entity is not alive at that point.
    /// Recording does not read the entity's current world state.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else if (!entity.IsPlaceholder)
        {
            GetOrCreateStore<T>().Append(entity, component, KindSet);
            StampRecordedFrame();
            FlagFrameIfMayContainPlaceholder(component);
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
    /// <remarks>
    /// Existing-entity liveness is evaluated when the stream is consumed. The
    /// command is silently discarded if the entity is not alive at that point.
    /// Recording does not read the entity's current world state.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        }
        else if (!entity.IsPlaceholder)
        {
            GetOrCreateStore<T>().AppendRemove(entity);
            StampRecordedFrame();
        }
    }

    /// <summary>
    /// Records a Destroy command for the specified entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Destroy(Entity entity)
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out _))
        {
            ThrowIfStaleFrame();
            CancelPendingEntity(entity);
        }
        else if (!entity.IsPlaceholder)
        {
            AppendDestroy(entity);
        }
    }

    /// <summary>
    /// Records a clone of one source entity and its current component values,
    /// including pending component changes.
    /// </summary>
    public Entity Clone(Entity source)
    {
        ThrowIfStaleFrame();
        if (IsSourceDestroyedThisFrame(source))
            throw new InvalidOperationException(
                $"Cannot clone entity {source}: it was destroyed in the same batch.");

        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(source, out var srcBatchIdx))
            return ClonePendingSource(source, srcBatchIdx);

        if (!_world.TryGetLocation(source, out var location))
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

        return CloneImpl(source, location);
    }
}
