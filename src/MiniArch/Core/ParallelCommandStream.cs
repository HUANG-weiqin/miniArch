using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Multi-threaded <see cref="CommandStreamCore"/>. All record methods
/// (<c>Create</c>, <c>Track</c>, <c>Add&lt;T&gt;</c>,
/// <c>Set&lt;T&gt;</c>, <c>Remove&lt;T&gt;</c>, <c>Destroy</c>,
/// <c>AddChild</c>, <c>RemoveChild</c>, <c>Clone</c>)
/// can be invoked concurrently from multiple threads.
/// <see cref="CommandStreamCore.Submit"/>, <see cref="CommandStreamCore.Snapshot"/>,
/// <see cref="CommandStreamCore.SnapshotInto"/>,
/// <see cref="CommandStreamCore.SubmitAndSnapshotAsync"/>,
/// <see cref="CommandStreamCore.SubmitAndSnapshotIntoAsync"/> and
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
public sealed partial class ParallelCommandStream : CommandStreamCore
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
    /// <para/>
    /// <b>Pending entity note:</b> If <paramref name="entity"/> is a pending
    /// (Create'd but not yet Submit'd/Snapshot'd) entity, this Add is folded
    /// with other Add/Set/Remove into the final materialized component
    /// signature. Intermediate operations are <b>not</b> observable via
    /// <c>ChangeWatch&lt;,&gt;.Diff</c> or <c>TransitionWatch&lt;&gt;.Diff</c>.
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
    /// <para/>
    /// <b>Pending entity note:</b> Same folding semantics as <see cref="Add{T}"/>.
    /// Multiple Sets on a pending entity collapse to the last value.
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
    /// <para/>
    /// <b>Pending entity note:</b> Same folding semantics as <see cref="Add{T}"/>.
    /// Remove on a pending entity is folded into the net component signature;
    /// no intermediate Exited transition is produced.
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
    /// <remarks>
    /// <para>
    /// <b>Limitations (parallel mode):</b>
    /// </para>
    /// <para>
    /// - <c>Clone</c> does <b>not</b> support pending (same-buffer created) source entities.
    ///   Use a single-threaded <see cref="CommandStream"/> when cloning pending entities,
    ///   or <c>Submit</c> first then clone the materialized result.
    /// </para>
    /// <para>
    /// - For materialized source entities, only the archetype storage is read
    ///   (no component-store overlay scan). This avoids ThreadLocal visibility issues
    ///   in parallel recording. Virtual hierarchy semantics (pending AddChild/RemoveChild)
    ///   are <b>not</b> applied —only the world hierarchy is used.
    /// </para>
    /// <para>
    /// Full virtual-state clone semantics (overlay scan + virtual hierarchy + pending source)
    /// are only available in the single-threaded <see cref="CommandStream"/>.
    /// </para>
    /// </remarks>
    public Entity Clone(Entity source)
    {
        // Destroy detection (read-only, safe outside lock)
        if (IsSourceDestroyedThisFrame(source))
            throw new InvalidOperationException(
                $"Cannot clone entity {source}: it was destroyed in the same batch.");

        // pending source NOT supported in parallel mode — component store uses
        // ThreadLocal append, clone-time snapshot cannot see concurrent writes reliably.
        if (TryGetPendingBatch(source, out _))
            throw new NotSupportedException(
                "ParallelCommandStream.Clone does not support pending source. " +
                "Use single-threaded CommandStream for pending-entity cloning, " +
                "or Submit first then clone the materialized result.");

        lock (_storeCreateLock)
        {
            if (!_world.TryGetLocation(source, out var location))
                throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

            // Materialized path: only read archetype storage (no overlay scan).
            // Children are cloned from world hierarchy only (no virtual view).
            var clone = CreateCore();
            int cloneBatchIdx;
            if (_deferredEntities)
                TryGetPendingBatch(clone, out cloneBatchIdx);
            else
                cloneBatchIdx = _frozen.PendingBatch[clone.Id];

            // Copy components from archetype only (no overlay)
            var archetype = location.Archetype;
            var sourceRow = location.RowIndex;
            var sig = archetype.Signature.AsSpan();
            for (var i = 0; i < sig.Length; i++)
            {
                var ct = sig[i];
                var size = ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(ct));
                var offset = ReserveBatchBufSpace(size);
                unsafe { fixed (byte* ptr = &_frozen.BatchBuf[offset]) archetype.ReadComponentRaw(i, sourceRow, ptr); }
                CommitBatchComponent(cloneBatchIdx, ct, offset, size);
            }

            // Clone children from world hierarchy only (no pending intents)
            CloneChildrenFromWorld(source, clone);

            return clone;
        }
    }

    /// <summary>
    /// Clones children of <paramref name="sourceRoot"/> using only the world hierarchy
    /// (no virtual view). Used by <see cref="Clone"/> in parallel mode.
    /// </summary>
    private void CloneChildrenFromWorld(Entity sourceRoot, Entity cloneRoot)
    {
        if (!_world.Hierarchy.HasChildren(_world, sourceRoot))
            return;

        var stack = ArrayPool<Entity>.Shared.Rent(32);
        var cloneStack = ArrayPool<Entity>.Shared.Rent(32);
        var stackCount = 0;

        try
        {
            foreach (var child in _world.Hierarchy.EnumerateChildren(_world, sourceRoot))
            {
                if (stackCount >= stack.Length)
                {
                    GrowPooled(ref stack, stackCount);
                    GrowPooled(ref cloneStack, stackCount);
                }
                stack[stackCount] = child;
                cloneStack[stackCount] = cloneRoot;
                stackCount++;
            }

            while (stackCount > 0)
            {
                stackCount--;
                var srcChild = stack[stackCount];
                var cloneParent = cloneStack[stackCount];

                if (!_world.TryGetLocation(srcChild, out var childLocation))
                    throw new InvalidOperationException(
                        $"Clone failed: child entity {srcChild} has no location. " +
                        "The source entity may be corrupted.");

                var cloneChild = CreateCore();
                int batchIdx;
                if (_deferredEntities)
                {
                    // CreateCore (via CreateDeferredImpl) always allocates a batch slot
                    // and sets _lastCreated, so TryGetPendingBatch must succeed.
                    if (!TryGetPendingBatch(cloneChild, out batchIdx))
                    {
                        throw new InvalidOperationException(
                            $"Clone: deferred clone child {cloneChild} has no pending batch slot.");
                    }
                }
                else
                    batchIdx = _frozen.PendingBatch[cloneChild.Id];

                var archetype = childLocation.Archetype;
                var sourceRow = childLocation.RowIndex;
                var sig = archetype.Signature.AsSpan();
                for (var i = 0; i < sig.Length; i++)
                {
                    var ct = sig[i];
                    var size = ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(ct));
                    var offset = ReserveBatchBufSpace(size);
                    unsafe { fixed (byte* ptr = &_frozen.BatchBuf[offset]) archetype.ReadComponentRaw(i, sourceRow, ptr); }
                    CommitBatchComponent(batchIdx, ct, offset, size);
                }
                AddChildCore(cloneParent, cloneChild);

                foreach (var grandChild in _world.Hierarchy.EnumerateChildren(_world, srcChild))
                {
                    if (stackCount >= stack.Length)
                    {
                        GrowPooled(ref stack, stackCount);
                        GrowPooled(ref cloneStack, stackCount);
                    }
                    stack[stackCount] = grandChild;
                    cloneStack[stackCount] = cloneChild;
                    stackCount++;
                }
            }
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(stack);
            ArrayPool<Entity>.Shared.Return(cloneStack);
        }
    }
}
