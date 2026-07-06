using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Multi-threaded <see cref="CommandStreamCore"/>. All record methods
/// (<c>Create</c>, <c>Track</c>, <c>Add&lt;T&gt;</c>,
/// <c>Set&lt;T&gt;</c>, <c>Remove&lt;T&gt;</c>, <c>Destroy</c>,
/// <c>AddChild</c>, <c>RemoveChild</c>, <c>Clone</c>)
/// can be invoked concurrently from multiple threads.
/// <see cref="CommandStreamCore.Submit"/>, <see cref="CommandStreamCore.Snapshot"/>,
/// <see cref="CommandStreamCore.SubmitAndSnapshotAsync"/> and
/// <see cref="CommandStreamCore.Replay"/> must be called from a single thread
/// after all parallel recording work has completed.
/// </summary>
/// <remarks>
/// <para>
/// For single-threaded use, prefer <see cref="CommandStream"/> —it avoids the
/// per-mutator lock acquire.
/// </para>
/// <para>
/// <b>Concurrency model:</b> all mutators serialize on
/// <c>_storeCreateLock</c>. Per-entity record order across threads is
/// non-deterministic. For the batch buffer (<c>Create</c>/<c>Clone</c>/<c>Destroy</c>),
/// commands are sorted and deduped before emission. For existing entity component
/// stores (<c>Add</c>/<c>Set</c>/<c>Remove</c>), conflicting commands on the same
/// entity are applied in ThreadLocal merge order —the caller is responsible for
/// avoiding or reconciling conflicts on the same entity.
/// </para>
/// <para>
/// Do <b>not</b> record concurrently into multiple <see cref="ParallelCommandStream"/>
/// instances that target the same <see cref="World"/>; concurrent recording is
/// only supported within one stream.
/// </para>
/// </remarks>
public sealed class ParallelCommandStream : CommandStreamCore
{
    /// <summary>
    /// Creates a new parallel command stream bound to <paramref name="world"/>.
    /// </summary>
    public ParallelCommandStream(World world) : base(world) { }

    /// <summary>
    /// Records a deferred entity creation and returns the new entity (placeholder or real).
    /// Thread-safe; serializes on the internal create lock.
    /// </summary>
    public Entity Create()
    {
        lock (_storeCreateLock)
            return CreateCore();
    }

    /// <summary>
    /// Creates a tracked handle for <paramref name="entity"/> that auto-updates
    /// when a deferred placeholder is resolved during Submit or Replay.
    /// Thread-safe; serializes on the internal create lock for placeholder registration.
    /// </summary>
    public EntitySlot Track(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return new EntitySlot(entity);

        // Placeholder slots register into _trackedBySeq — serialize with other mutators.
        lock (_storeCreateLock)
            return TrackCore(entity);
    }

    /// <summary>
    /// Records an Add command for the specified component on the given entity.
    /// Thread-safe; checks pending batch under the lock, falls back to
    /// per-component concurrent store append.
    /// </summary>
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        lock (_storeCreateLock)
        {
            if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            {
                WritePendingComponent(batchIdx, component);
                return;
            }

            if (entity.IsPlaceholder || !_world.IsAlive(entity))
                return;
        }
        GetOrCreateStoreParallel<T>().AppendConcurrent(entity, component, KindAdd);
    }

    /// <summary>
    /// Records a Set command for the specified component on the given entity.
    /// Thread-safe; checks pending batch under the lock, falls back to
    /// per-component concurrent store append.
    /// </summary>
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        lock (_storeCreateLock)
        {
            if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            {
                WritePendingComponent(batchIdx, component);
                return;
            }

            if (entity.IsPlaceholder || !_world.IsAlive(entity))
                return;
        }
        GetOrCreateStoreParallel<T>().AppendConcurrent(entity, component, KindSet);
    }

    /// <summary>
    /// Records a Remove command for the specified component type from the given entity.
    /// Thread-safe; checks pending batch under the lock, falls back to
    /// per-component concurrent store append.
    /// </summary>
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        lock (_storeCreateLock)
        {
            if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
            {
                MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
                return;
            }

            if (entity.IsPlaceholder || !_world.IsAlive(entity))
                return;
        }
        GetOrCreateStoreParallel<T>().AppendConcurrent(entity, default!, KindRemove);
    }

    /// <summary>
    /// Records a Destroy command for the specified entity.
    /// Thread-safe; serializes on the internal create lock.
    /// </summary>
    public void Destroy(Entity entity)
    {
        // Same pending-check + cancel logic as single-threaded, under the lock.
        // Without this, parallel Destroy on a pending entity would append to
        // DestroyEntities without cancelling the batch, causing the entity (and
        // its pending descendants) to be materialized then destroyed —diverging
        // from single-threaded semantics where they are never materialized.
        lock (_storeCreateLock)
            DestroyCore(entity);
    }

    /// <summary>
    /// Records an AddChild command establishing a parent-child relationship.
    /// Thread-safe; serializes on the internal create lock.
    /// </summary>
    public void AddChild(Entity parent, Entity child)
    {
        lock (_storeCreateLock)
            AddChildCore(parent, child);
    }

    /// <summary>
    /// Records a RemoveChild command detaching the entity from its parent.
    /// Thread-safe; serializes on the internal create lock.
    /// </summary>
    public void RemoveChild(Entity child)
    {
        lock (_storeCreateLock)
            RemoveChildCore(child);
    }

    /// <summary>
    /// Records a clone of the source entity, including all components and descendants.
    /// Thread-safe; validates outside the lock, materializes under it.
    /// </summary>
    public Entity Clone(Entity source)
    {
        // Validate outside the lock (read-only world access); materialize under it.
        if (!_world.TryGetLocation(source, out var location))
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

        lock (_storeCreateLock)
            return CloneImpl(source, location);
    }
}
