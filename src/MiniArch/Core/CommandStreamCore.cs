using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Shared core for <see cref="CommandStream"/> (single-threaded) and
/// <see cref="ParallelCommandStream"/>. Owns all recording buffers, emit/submit
/// machinery, EntitySlot tracking, and the archetype mask cache.
/// <para/>
/// Subclasses are responsible <b>only</b> for the 9 public mutators
/// (<c>Create</c>, <c>Track</c>, <c>Add&lt;T&gt;</c>,
/// <c>Set&lt;T&gt;</c>, <c>Remove&lt;T&gt;</c>, <c>Destroy</c>,
/// <c>AddChild</c>, <c>RemoveChild</c>, <c>Clone</c>):
/// they pick the synchronization strategy (none vs lock) and dispatch to the
/// <c>*Core</c> helpers in this base class. Everything else —Submit, Snapshot,
/// Replay, async paths, materialization, FrameDelta emission— is shared.
/// </summary>
// Visibility is public so that public sealed subclasses (CommandStream,
// ParallelCommandStream) can derive from it. The class is abstract, so external
// callers cannot instantiate it directly; all internal state stays inaccessible.
public abstract class CommandStreamCore
{
    protected readonly World _world;
    // Pooled mutable buffer bundle. _frozen holds the current recording state;
    // _spareFrozen is a recycled standby (when the background worker is done);
    // _pendingFrozen holds state handed off to the background BuildFromFrozen worker.
    // A single reference swap in SwapOutState replaces the old 13-field-by-field
    // swap, eliminating a class of silent correctness bugs that only surface on
    // the async path.
    private protected FrozenState _frozen;
    private protected FrozenState? _spareFrozen;
    private protected FrozenState? _pendingFrozen;
    private protected Task? _pendingTask;

    // Scalars that live outside FrozenState (only needed during recording/reset,
    // not by the background worker).
    protected int _pendingBatchMin = int.MaxValue;
    protected int _pendingBatchMax;
    protected int _batchCompTotal;
    protected int _batchBufLen;

    // Local archetype cache keyed by ComponentMask.
    protected const int MaskCacheSize = 8;
    private protected MaskCacheSlot[] _maskCache = [];
    private protected int _maskCacheCount;
    private protected int _maskCacheGeneration = -1;
    private protected ComponentMask _lastMask;
    private protected Archetype? _lastMaskArchetype;

    private protected readonly record struct MaskCacheSlot(ComponentMask Mask, Archetype Archetype);

    // Fast path for Add/Set immediately after Create on the same entity.
    protected Entity _lastCreated;
    protected int _lastCreatedBatch = -1;

    // 2-slot LRU cache for GetOrCreateStore<T>() — avoids Stores array lookup
    // on repeated/alternating Set<T>/Add<T>/Remove<T> calls.
    private int _lastStoreId0 = -1;
    private ComponentStore? _lastStore0;
    private int _lastStoreId1 = -1;
    private ComponentStore? _lastStore1;

    // Dirty flags to avoid scanning _frozen.Stores when there are no
    // component store commands (common no-op Submit case).
    private bool _hasStoreCommands;
    private bool _hasParallelStoreWrites;

    // Lock used by <see cref="ParallelCommandStream"/> for serializing mutators
    // that touch shared FrozenState arrays. Single-threaded subclasses never
    // acquire it; the field exists so protected helpers can take it when needed.
    private protected readonly object _storeCreateLock = new();

    // ── EntitySlot tracking ──────────────────────────────────────────
    // Registration array indexed by placeholder seq. Each entry is a linked
    // list of Slot objects that want to be notified when this seq is resolved.
    // Cleared after each resolution pass.
    private protected EntitySlot.Slot?[] _trackedBySeq = [];
    private protected int _trackedMaxSeq;
    // Saved by Clear() for the Snapshot+Clear+Replay path. When Clear() is
    // called after Snapshot(), the tracked registrations are preserved here
    // so Replay() can resolve them after the underlying ReplayCore call.
    private protected EntitySlot.Slot?[]? _replayTrackedBySeq;
    private protected int _replayTrackedMaxSeq;
    // Set by Snapshot(), consumed by Clear(). When true, Clear() preserves
    // _trackedBySeq into _replayTrackedBySeq for subsequent Replay().
    // When false (e.g. user abandons a frame), registrations are dropped.
    private protected bool _pendingReplay;

    private protected static void GrowPooled<T>(ref T[] array, int count)
    {
        var grown = ArrayPool<T>.Shared.Rent(count * 2);
        Array.Copy(array, grown, count);
        ArrayPool<T>.Shared.Return(array);
        array = grown;
    }

    // ── Construction ───────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance bound to the specified world for deferred command recording.
    /// </summary>
    private protected CommandStreamCore(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _frozen = new FrozenState(ComponentRegistry.Shared.ComponentTypeCount);
    }

    /// <summary>
    /// When <c>true</c>, <c>Create</c> and <c>Clone</c> produce
    /// placeholder entities (negative ids) instead of allocating real ids from
    /// the host <see cref="World"/>. <see cref="Snapshot"/> then emits a
    /// placeholder-id <see cref="FrameDelta"/> suitable for multi-host lockstep
    /// where each peer owns an independent <see cref="World"/> and id allocator.
    /// When <c>false</c> (default), <c>Create</c> and <c>Clone</c>
    /// allocate real ids immediately, and <see cref="Snapshot"/> resolves any
    /// deferred placeholders before building a real-id delta.
    ///
    /// When <see cref="DeferredEntities"/> is <c>true</c>, component fields of type
    /// <see cref="Entity"/> that reference deferred-created entities are
    /// automatically resolved by both <see cref="Submit"/> and
    /// <see cref="Replay(FrameDelta, Boolean)"/>.
    /// You can freely store a placeholder returned by <c>Create</c>
    /// in another component's <see cref="Entity"/> field; the system
    /// replaces it with the real entity ID at apply time.
    /// </summary>
    /// <example>
    /// <code>
    /// var stream = new CommandStream(world) { DeferredEntities = true };
    /// var target = stream.Create();
    /// var follower = stream.Create();
    /// stream.Add(follower, new AIFollow { Target = target });
    /// // Target is a placeholder —resolved automatically on Submit/Replay.
    /// </code>
    /// </example>
    public bool DeferredEntities
    {
        get => _deferredEntities;
        set => _deferredEntities = value;
    }

    private protected bool _deferredEntities;

    // ── Record API ────────────────────────────────────────────────────

    /// <summary>
    /// Core create logic shared by both subclasses (no synchronization). Subclasses
    /// call this from their own public <c>Create</c> method, adding synchronization
    /// as needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected Entity CreateCore() => _deferredEntities ? CreateDeferredImpl() : CreateImpl();

    // ── EntitySlot API ───────────────────────────────────────────────

    /// <summary>
    /// Core track logic for placeholder entities. Caller handles synchronization.
    /// Subclasses call this from their own public <c>Track</c> method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected EntitySlot TrackCore(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return new EntitySlot(entity);

        var slot = new EntitySlot.Slot { Entity = entity };
        var seq = entity.Version;
        RegisterTrackedSlot(slot, seq);
        return new EntitySlot(slot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RegisterTrackedSlot(EntitySlot.Slot slot, int seq)
    {
        EnsureCapacity(ref _trackedBySeq, seq, 16);
        slot.Next = _trackedBySeq[seq];
        _trackedBySeq[seq] = slot;
        if (seq >= _trackedMaxSeq) _trackedMaxSeq = seq + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateImpl()
    {
        var entity = _world.ReserveDeferredEntityUnsafe();
        GrowPendingBatchFor(entity.Id);
        var batchIdx = AllocBatchSlot(entity);
        _frozen.PendingBatch[entity.Id] = batchIdx;
        if (entity.Id < _pendingBatchMin) _pendingBatchMin = entity.Id;
        if (entity.Id >= _pendingBatchMax) _pendingBatchMax = entity.Id + 1;
        _lastCreated = entity;
        _lastCreatedBatch = batchIdx;
        return entity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateDeferredImpl()
    {
        var seq = _deferredSeq++;
        var placeholder = new Entity(-1, seq);
        EnsureCapacity(ref _pendingBatchDeferredArr, seq, 64);
#if DEBUG
        EnsureCapacity(ref _pendingBatchDeferredEpoch, seq, 64);
        _pendingBatchDeferredEpoch[seq] = _world._deferredEpoch;
#endif
        var batchIdx = AllocBatchSlot(placeholder);
        _pendingBatchDeferredArr[seq] = batchIdx;
        _lastCreated = placeholder;
        _lastCreatedBatch = batchIdx;
        return placeholder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AllocBatchSlot(Entity entity)
    {
        EnsureCapacity(ref _frozen.BatchHeads, _frozen.PendingBatchCount, 16);
        EnsureCapacity(ref _frozen.BatchCompCounts, _frozen.PendingBatchCount, 16);
        if (_frozen.PendingBatchCount >= _frozen.BatchEntities.Length)
            Array.Resize(ref _frozen.BatchEntities, _frozen.BatchHeads.Length);
        if (_frozen.PendingBatchCount >= _frozen.BatchCanceled.Length)
            Array.Resize(ref _frozen.BatchCanceled, _frozen.BatchHeads.Length);
        var batchIdx = _frozen.PendingBatchCount++;
        _frozen.BatchHeads[batchIdx] = -1;
        _frozen.BatchCompCounts[batchIdx] = 0;
        _frozen.BatchEntities[batchIdx] = entity;
        _frozen.BatchCanceled[batchIdx] = false;
        return batchIdx;
    }

    /// <summary>
    /// Core destroy logic. Caller handles synchronization.
    /// Subclasses call this from their own public <c>Destroy</c> method.
    /// </summary>
    protected void DestroyCore(Entity entity)
    {
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out _))
        {
            CancelPendingEntity(entity);
            CancelPendingDescendants(entity);
        }
        else if (!entity.IsPlaceholder)
        {
            AppendDestroy(entity);
        }
    }

    /// <summary>
    /// Core AddChild logic. Caller handles synchronization.
    /// Subclasses call this from their own public <c>AddChild</c> method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void AddChildCore(Entity parent, Entity child)
        => _frozen.HierarchyByChild[child] = new HierarchyIntent(true, parent);

    /// <summary>
    /// Core RemoveChild logic. Caller handles synchronization.
    /// Subclasses call this from their own public <c>RemoveChild</c> method.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void RemoveChildCore(Entity child)
        => _frozen.HierarchyByChild[child] = new HierarchyIntent(false, default);

    /// <summary>
    /// Core clone logic. Caller handles synchronization.
    /// Subclasses call this from their own public <c>Clone</c> method.
    /// </summary>
    protected Entity CloneCore(Entity source)
    {
        // Destroy detection FIRST: if source is in DestroyEntities or its pending batch is canceled
        if (IsSourceDestroyedThisFrame(source))
            throw new InvalidOperationException(
                $"Cannot clone entity {source}: it was destroyed in the same batch.");

        // 1. Check if source is a pending entity (created in same buffer)
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(source, out var srcBatchIdx))
            return ClonePendingSource(source, srcBatchIdx);

        // 2. Fall through to materialized path
        if (!_world.TryGetLocation(source, out var location))
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

        return CloneImpl(source, location);
    }

    private protected bool IsSourceDestroyedThisFrame(Entity source)
        => IsDestroyedThisFrame(source, _frozen);

    private protected Entity CloneImpl(Entity source, EntityInfo info)
    {
        if (_deferredEntities)
        {
            var clone = CreateDeferredImpl();
            // CreateDeferredImpl always allocates a batch slot and publishes it
            // via _lastCreated, so TryGetPendingBatch must succeed. Check the
            // return value explicitly instead of relying on the implicit
            // invariant — a future change that stops setting _lastCreated would
            // otherwise silently pass batchIdx = -1 into CloneComponents and
            // corrupt the batch arrays.
            if (!TryGetPendingBatch(clone, out var batchIdx))
            {
                throw new InvalidOperationException(
                    $"Clone: deferred clone {clone} has no pending batch slot.");
            }
            CloneComponents(source, info, clone, batchIdx);
            return clone;
        }
        return CloneImplImmediate(source, info);
    }

    private Entity CloneImplImmediate(Entity source, EntityInfo info)
    {
        var clone = CreateImpl();
        var batchIdx = _frozen.PendingBatch[clone.Id];
        CloneComponents(source, info, clone, batchIdx);
        return clone;
    }

    /// <summary>
    /// Copies components from a materialized (world-archetype) source entity into the clone's
    /// batch buffer. Reads archetype storage + pending component store overlays via
    /// <see cref="ComponentMerger"/>. Does NOT recurse into children — the caller is
    /// responsible for that.
    /// </summary>
    private void CloneMaterializedComponents(Entity source, EntityInfo info, int batchIdx)
    {
        var archetype = info.Archetype;
        var sourceRow = info.RowIndex;
        var components = archetype.Signature.AsSpan();

        // Step 1: stackalloc arrays for merged components (no heap List)
        Span<ComponentType> mergedTypes = stackalloc ComponentType[64];
        Span<int> mergedOffsets = stackalloc int[64];
        Span<int> mergedSizes = stackalloc int[64];
        var count = 0;

        for (var i = 0; i < components.Length; i++)
        {
            var ct = components[i];
            var size = ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(ct));
            var offset = ReserveBatchBufSpace(size);
            unsafe
            {
                fixed (byte* ptr = &_frozen.BatchBuf[offset])
                    archetype.ReadComponentRaw(i, sourceRow, ptr);
            }
            mergedTypes[count] = ct;
            mergedOffsets[count] = offset;
            mergedSizes[count] = size;
            count++;
        }

        // Step 2: Create ONE ComponentMerger and scan all stores (no per-store OverlayCollector)
        var merger = new ComponentMerger(this, mergedTypes, mergedOffsets, mergedSizes, ref count);

        try
        {
            var stores = _frozen.Stores;
            for (var s = 0; s < stores.Length; s++)
            {
                var store = stores[s];
                if (store is null) continue;
                store.ForEachEntityEntry(source, ref merger);
            }

            // Step 3: Read final arrays from merger (may have grown past stackalloc)
            var finalTypes = merger.Types;
            var finalOffsets = merger.Offsets;
            var finalSizes = merger.Sizes;
            var finalCount = merger.Count;

            for (var i = 0; i < finalCount; i++)
            {
                CommitBatchComponent(batchIdx, finalTypes[i], finalOffsets[i], finalSizes[i]);
            }
        }
        finally
        {
            merger.ReturnRented();
        }
    }

    private void CloneComponents(Entity source, EntityInfo info, Entity clone, int batchIdx)
    {
        CloneMaterializedComponents(source, info, batchIdx);
        CloneChildrenFromVirtualHierarchy(source, clone);
    }

    private Entity ClonePendingSource(Entity source, int srcBatchIdx)
    {
        // Create clone entity (same deferred mode as source)
        var clone = _deferredEntities ? CreateDeferredImpl() : CreateImpl();
        var cloneBatchIdx = clone.Id >= 0
            ? _frozen.PendingBatch[clone.Id]
            : _pendingBatchDeferredArr[clone.Version];

        // Copy components from source's batch chain into clone's batch
        CopyComponentsFromBatch(source, srcBatchIdx, clone, cloneBatchIdx);

        // Clone children via virtual hierarchy view (world children + pending intents)
        CloneChildrenFromVirtualHierarchy(source, clone);

        return clone;
    }

    private void CopyComponentsFromBatch(Entity source, int srcBatchIdx, Entity clone, int cloneBatchIdx)
    {
        var head = _frozen.BatchHeads[srcBatchIdx];
        var comps = _frozen.BatchComps;
        var buf = _frozen.BatchBuf;
        var rawCount = _frozen.BatchCompCounts[srcBatchIdx];

        int[]? pooledIndices = null;
        Span<int> indices = rawCount <= 64
            ? stackalloc int[rawCount]
            : (pooledIndices = ArrayPool<int>.Shared.Rent(rawCount)).AsSpan(0, rawCount);
        try
        {
            var idx = DeduplicateBatchChain(comps, head, indices);

            // Copy deduplicated components into clone's batch buffer
            for (var i = 0; i < idx; i++)
            {
                ref var comp = ref comps[indices[i]];
                var offset = ReserveBatchBufSpace(comp.Size);
                buf.AsSpan(comp.Offset, comp.Size).CopyTo(new Span<byte>(buf, offset, comp.Size));
                CommitBatchComponent(cloneBatchIdx, comp.Type, offset, comp.Size);
            }
        }
        finally
        {
            if (pooledIndices is not null)
                ArrayPool<int>.Shared.Return(pooledIndices);
        }
    }

    // ── Submit ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies all recorded commands to the world and returns true if any work was performed.
    /// </summary>
    public bool Submit()
    {
        PrepareStores();
        if (!HasAnyCommands())
            return false;

        var submitted = false;
        try
        {
            // Order matches BuildDelta: Create —Hierarchy —Ops —Destroy.
            // Keeping Submit and Snapshot aligned lets hosts use Submit on source and
            // Replay on replica without diverging for combined command patterns.
            //
            // Before any free-list mutations, align the cancelled-batch entries to
            // match the wire emission order. CancelPendingEntity pushes free-list
            // entries in user destroy-order during recording, but Replay processes
            // Release ops in batch (creation) order. The batch-order realignment
            // below corrects this divergence so the source's free-list matches the
            // shadow's after Replay.
            AlignCancelledBatchFreeListOrder();
            ResolveDeferredCreates();
            MaterializeAllPending();
            ApplyHierarchy();
            ApplyComponentStores();
            ApplyDestroys();
            submitted = true;
        }
        finally
        {
            Clear(releaseReserved: !submitted);
        }
        return true;
    }

    /// <summary>
    /// Realigns free-list entries for cancelled pending entities to match the
    /// batch (creation) order, ensuring the source's free-list ordering is
    /// identical to the shadow's after Replay.
    /// </summary>
    /// <remarks>
    /// When a pending entity is destroyed during recording,
    /// <see cref="CancelPendingEntity"/> calls
    /// <see cref="World.ReleaseReservedEntity"/> immediately, pushing the
    /// entity's slot to the free-list in user destroy-order. The FrameDelta
    /// wire, however, emits Release ops in batch (creation) order. During
    /// Replay, the shadow processes these Release ops in batch order.
    /// When creates and cancels are interleaved in different user ordering,
    /// the free-list tail diverges —adjacent entries are swapped.
    ///
    /// This method walks cancelled batches in creation order and re-appends
    /// each cancelled entity's free-list entry (if still present —it may have
    /// been consumed by a later Create). The result: cancelled entities that
    /// survive to frame end in the free-list are ordered by batch index, matching
    /// the Replay's Release order. Regular destroys (non-pending) are pushed
    /// later by <see cref="ApplyDestroys"/>, also in emit order.
    /// </remarks>
    private void AlignCancelledBatchFreeListOrder()
    {
        if (_frozen.CancelledBatchCount == 0)
            return;

        // Walk cancelled batches in creation (batch) order.
        for (var i = 0; i < _frozen.PendingBatchCount; i++)
        {
            if (!_frozen.BatchCanceled[i])
                continue;

            var entity = _frozen.BatchEntities[i];
            if (entity.Id < 0)
                continue; // deferred placeholder —no free-list push during record

            // CancelPendingEntity increments version before pushing.
            var expectedVersion = entity.Version == int.MaxValue ? 1 : entity.Version + 1;
            _world.RepushFreeEntry(entity.Id, expectedVersion);
        }
    }

    private void AlignCancelledBatchFreeListOrderForFrozen(FrozenState frozen)
    {
        if (frozen.CancelledBatchCount == 0)
            return;

        for (var i = 0; i < frozen.PendingBatchCount; i++)
        {
            if (!frozen.BatchCanceled[i])
                continue;

            var entity = frozen.BatchEntities[i];
            if (entity.Id < 0)
                continue;

            var expectedVersion = entity.Version == int.MaxValue ? 1 : entity.Version + 1;
            _world.RepushFreeEntry(entity.Id, expectedVersion);
        }
    }

    private bool HasAnyCommands()
    {
        if (_frozen.PendingBatchCount > 0 || _frozen.DestroyCount > 0 || _frozen.HierarchyByChild.Count > 0)
            return true;
        if (_hasStoreCommands)
            return true;
        return false;
    }

    private void MaterializeAllPending()
    {
        var view = new PendingBatchView(
            _frozen.BatchCanceled, _frozen.BatchHeads, _frozen.BatchCompCounts,
            _frozen.BatchComps, _frozen.BatchBuf, _frozen.BatchEntities, _frozen.PendingBatchCount);
        for (var i = 0; i < _frozen.PendingBatchCount; i++)
        {
            if (_frozen.BatchCanceled[i]) continue;
            MaterializePending(view, view.Entities[i], i);
        }
    }

    private void SealParallelStores()
    {
        if (!_hasParallelStoreWrites)
            return;
        foreach (var store in _frozen.Stores)
            store?.SealParallelWrites();
        // Flag is consumed; reset so the next cycle starts clean.
        _hasParallelStoreWrites = false;
    }

    private void ApplyComponentStores()
    {
        foreach (var store in _frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.ApplyToWorld(_world);
        }
    }

    private void ApplyDestroys()
    {
        for (var i = 0; i < _frozen.DestroyCount; i++)
        {
            var entity = _frozen.DestroyEntities[i];
            // The entity may already have been cascade-destroyed if a parent
            // earlier in the DestroyEntities array was destroyed (DestroySingle
            // recursively destroys all descendants). Check liveness to match
            // the Replay path, which also guards with IsAlive.
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }
    }

    private void ApplyHierarchy()
    {
        ApplyHierarchyToWorld(_world, _frozen);
    }

    // ── Snapshot / SubmitAndSnapshotAsync ─────────────────────────────

    /// <summary>
    /// Produces a <see cref="FrameDelta"/> from the recorded commands without
    /// applying them to the local <see cref="World"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see cref="DeferredEntities"/> is <c>false</c> (default), deferred
    /// placeholders are first resolved into host-local real ids, producing a
    /// <b>real-id delta</b>. This is the original single-host behavior.
    /// </para>
    /// <para>
    /// When <see cref="DeferredEntities"/> is <c>true</c>, the delta contains
    /// <b>placeholder</b> entities (negative ids). Each replaying host assigns
    /// its own local ids in deterministic order, making this the lockstep-safe
    /// code path for multi-host scenarios.
    /// </para>
    /// <para>
    /// For the relay-only flow (produce delta, do not apply locally),
    /// call <c>Snapshot()</c> then <c>Clear()</c>. The source host must
    /// replay the <b>local</b> delta object (the one returned by
    /// <c>Snapshot</c>) —not a deserialized copy received via network.
    /// Only the local object retains the internal marker that triggers
    /// tracked <see cref="EntitySlot"/> resolution. Peer hosts receive
    /// serialized copies (which lose the marker) and have no tracked
    /// slots to resolve. World-state convergence is identical either way:
    /// <see cref="Replay(FrameDelta, Boolean)"/> processes the same byte payload.
    /// </para>
    /// </remarks>
    public FrameDelta Snapshot()
    {
        PrepareStores();
        if (!_deferredEntities)
        {
            ResolveDeferredCreates();
            var delta = new FrameDelta();
            BuildDelta(delta);
            _pendingReplay = true;
            return delta;
        }
        ThrowIfSnapshotHasImmediateEntities();
        var d = new FrameDelta();
        BuildDelta(d);
        _pendingReplay = true;
        return d;
    }

    /// <summary>
    /// Writes the current snapshot into an existing <see cref="FrameDelta"/>,
    /// reusing its internal buffer. After warmup (buffer sized), repeated
    /// calls are zero-allocation —no new <see cref="FrameDelta"/> object header.
    /// </summary>
    /// <remarks>
    /// Prefer this over <see cref="Snapshot"/> in hot loops where you hold a
    /// persistent <c>FrameDelta</c> instance. Behavior is identical to
    /// <see cref="Snapshot"/> except the result is written into <paramref name="target"/>.
    /// <para/>
    /// The caller must not mutate <paramref name="target"/> concurrently.
    /// </remarks>
    public void SnapshotInto(FrameDelta target)
    {
        PrepareStores();
        if (!_deferredEntities)
        {
            ResolveDeferredCreates();
            target.Clear();
            BuildDelta(target);
            _pendingReplay = true;
            return;
        }
        ThrowIfSnapshotHasImmediateEntities();
        target.Clear();
        BuildDelta(target);
        _pendingReplay = true;
    }

    // ── Replay ───────────────────────────────────────────────────────

    /// <summary>
    /// Replays a <see cref="FrameDelta"/> into the underlying <see cref="World"/>.
    /// </summary>
    /// <param name="delta">The delta to replay.</param>
    /// <param name="resolveSlots">When <c>true</c>, resolves all tracked
    /// <see cref="EntitySlot"/>s using the placeholder map produced by this
    /// replay. Pass <c>true</c> only for your own delta —the delta whose
    /// placeholders you registered via <c>Track</c>.</param>
    /// <remarks>
    /// In a lockstep setup, replay all peer deltas with
    /// <paramref name="resolveSlots"/> = <c>false</c> (the default), and
    /// replay your own delta with <c>true</c>.
    /// <para>
    /// Note: <see cref="World"/> no longer exposes a public Replay method.
    /// Use this method to replay deltas.
    /// </para>
    /// </remarks>
    public void Replay(FrameDelta delta, bool resolveSlots = false)
    {
        _world.ReplayCore(delta);

        if (resolveSlots)
            ResolveTrackedSlotsFromReplay();
    }

    private void ThrowIfSnapshotHasImmediateEntities()
    {
        for (var i = 0; i < _frozen.PendingBatchCount; i++)
        {
            if (_frozen.BatchCanceled[i]) continue;
            if (_frozen.BatchEntities[i].Id >= 0)
                throw new InvalidOperationException(
                    "Snapshot() with DeferredEntities=true contains immediate entities. " +
                    "Use Submit() / SubmitAndSnapshotAsync() for single-host real-id scenarios.");
        }
    }

    /// <summary>
    /// Submits recorded commands to the local <see cref="World"/> and
    /// simultaneously builds a <see cref="FrameDelta"/> on a background
    /// thread. The returned delta always contains <b>real</b> entity ids
    /// because the host world owns the authoritative id allocator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the code path for an authoritative server that wants to
    /// apply changes locally while also forwarding a delta to mirror
    /// clients that must maintain id synchronization with the server.
    /// </para>
    /// <para>
    /// Always produces a <b>real-id delta</b> —deferred placeholders are
    /// resolved into the host's own ids before building the delta,
    /// regardless of <see cref="DeferredEntities"/>. Mirror clients
    /// replaying this delta must have an id allocator synchronized with
    /// the server (e.g. by replaying every frame from frame 0). For
    /// multi-host lockstep where each peer owns an independent world,
    /// use <see cref="Snapshot"/> with
    /// <see cref="DeferredEntities"/> set to <c>true</c> instead.
    /// </para>
    /// </remarks>
    public Task<FrameDelta> SubmitAndSnapshotAsync()
    {
        PrepareStores();
        if (!HasAnyCommands())
            return Task.FromResult(new FrameDelta());

        var frozen = SwapOutState();
#if DEBUG
        _world._deferredEpoch++;
        _pendingBatchDeferredEpoch = [];
#endif
        // Static delegate + state parameter avoids the per-call closure allocation
        // that Task.Run(() => ...) would create. FrozenState is a reference type,
        // so passing it as `object` is a free upcast —no boxing.
        var task = Task.Factory.StartNew(
            s_buildFromFrozen, frozen, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        // Align free-list entries for cancelled batches to match the wire
        // (batch) order before processing the frozen state.
        AlignCancelledBatchFreeListOrderForFrozen(frozen);
        SubmitFromFrozen(frozen);
        _pendingFrozen = frozen;
        _pendingTask = task;
        return task;
    }

    /// <summary>
    /// Combines <see cref="Submit"/> and <see cref="SnapshotInto"/> into a
    /// single async operation. Commands are submitted to the local
    /// <see cref="World"/> on the calling thread while the delta is built
    /// concurrently on a background thread, writing into <paramref name="target"/>
    /// to avoid allocating a new <see cref="FrameDelta"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After warmup, repeated calls avoid allocating a new
    /// <see cref="FrameDelta"/> object header —only one small boxed tuple is
    /// allocated per call (the frozen state and target passed to the background
    /// worker).
    /// </para>
    /// <para>
    /// The caller must not mutate <paramref name="target"/> until the returned
    /// <see cref="Task"/> completes.
    /// </para>
    /// <para>
    /// The delta always uses real (non-placeholder) entity ids, regardless of
    /// <see cref="DeferredEntities"/>. Mirror clients replaying this delta must
    /// have an id allocator synchronized with the server (e.g. by replaying
    /// every frame from frame 0). For multi-host lockstep where each peer owns
    /// an independent world, use <see cref="Snapshot"/> / <see cref="SnapshotInto"/>
    /// with <see cref="DeferredEntities"/> set to <c>true</c> instead.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A <see cref="Task"/> that completes when the background delta-building
    /// work is done. The caller should await this task before reading
    /// <paramref name="target"/>. The submit (world apply) runs synchronously
    /// on the calling thread before the returned task.
    /// </returns>
    public Task SubmitAndSnapshotIntoAsync(FrameDelta target)
    {
        PrepareStores();
        if (!HasAnyCommands())
        {
            target.Clear();
            return Task.CompletedTask;
        }

        var frozen = SwapOutState();
#if DEBUG
        _world._deferredEpoch++;
        _pendingBatchDeferredEpoch = [];
#endif
        var task = Task.Factory.StartNew(
            s_buildFromFrozenInto, (frozen, target), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        // Align free-list entries for cancelled batches to match the wire
        // (batch) order before processing the frozen state.
        AlignCancelledBatchFreeListOrderForFrozen(frozen);
        SubmitFromFrozen(frozen);
        _pendingFrozen = frozen;
        _pendingTask = task;
        return task;
    }

    private static readonly Func<object?, FrameDelta> s_buildFromFrozen =
        state => BuildFromFrozen((FrozenState)state!);

    private static readonly Action<object?> s_buildFromFrozenInto = state =>
    {
        var (frozen, target) = ((FrozenState, FrameDelta))state!;
        BuildFromFrozenInto(frozen, target);
    };

    private void BuildDelta(FrameDelta delta)
    {
        // Order matches Submit: Create —Hierarchy —Ops —Destroy.
        EmitPendingEntitiesToDelta(delta, new PendingBatchView(
            _frozen.BatchCanceled, _frozen.BatchHeads, _frozen.BatchCompCounts,
            _frozen.BatchComps, _frozen.BatchBuf, _frozen.BatchEntities, _frozen.PendingBatchCount),
            _deferredEntities);

        EmitHierarchyToDelta(delta, _frozen);

        foreach (var store in _frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.EmitToDelta(delta);
        }

        for (var i = 0; i < _frozen.DestroyCount; i++)
            delta.AddDestroy(_frozen.DestroyEntities[i]);
    }

    private void PruneStaleComponentCommands()
    {
        foreach (var store in _frozen.Stores)
            store?.PruneStaleCommands(_world);
    }

    /// <summary>
    /// Seals parallel stores then prunes stale component commands.
    /// Must be called before any Submit/Snapshot/SnapshotInto/SubmitAndSnapshotAsync/
    /// SubmitAndSnapshotIntoAsync operation. Not needed before Replay (no recording
    /// state to prepare).
    /// </summary>
    private void PrepareStores()
    {
        SealParallelStores();
        PruneStaleComponentCommands();
    }

    private void TryReclaimPending()
    {
        // Task.IsCompleted implies the worker thread has stopped reading frozen,
        // so we can safely hand the whole FrozenState (arrays + containers) to
        // the spare slot for the next SwapOutState to recycle.
        if (_pendingFrozen is null || _pendingTask is not { IsCompleted: true })
            return;

        _spareFrozen = _pendingFrozen;
        _pendingFrozen = null;
        _pendingTask = null;

#if DEBUG
        // Reclaimed — will become the active _frozen on next SwapOutState.
        _spareFrozen._isReadOnly = false;
        foreach (var store in _spareFrozen.Stores)
            if (store is not null) store._isReadOnly = false;
#endif
    }

    private FrozenState SwapOutState()
    {
        TryReclaimPending();
        ResolveDeferredCreates();

        FrozenState frozen;
        if (_spareFrozen is { } spare)
        {
            // Steady state: swap state-object references in one operation.
            // No field-by-field swap, no risk of missing a field.
            _spareFrozen = null;
            frozen = _frozen;
            _frozen = spare;

            // The recycled Stores array may predate the current ComponentTypeCount.
            var typeCount = ComponentRegistry.Shared.ComponentTypeCount;
            if (_frozen.Stores.Length < typeCount)
                Array.Resize(ref _frozen.Stores, typeCount);
        }
        else
        {
            // First call or worker hasn't finished: the old _frozen becomes
            // the returned snapshot; _frozen gets a fresh bundle.
            frozen = _frozen;
            _frozen = new FrozenState(ComponentRegistry.Shared.ComponentTypeCount);
        }

        // Reset the now-current state. Underlying arrays may carry stale data from
        // two frames ago, but every reader indexes by count and every allocator
        // re-initialises the slot before exposing it, so stale data is never observed.
        foreach (var store in _frozen.Stores)
            store?.Clear();

        _frozen.DestroyCount = 0;
        _frozen.PendingBatchCount = 0;
        _frozen.CancelledBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
        _batchBufLen = 0;
        _lastCreated = default;
        _lastCreatedBatch = -1;
        _lastStoreId0 = -1; _lastStore0 = null;
        _lastStoreId1 = -1; _lastStore1 = null;
        _hasStoreCommands = false;
        _hasParallelStoreWrites = false;
        _frozen.HierarchyByChild.Clear();

#if DEBUG
        // The swapped-out state is now read-only — neither SubmitFromFrozen
        // nor the background BuildFromFrozen task should mutate it.
        frozen._isReadOnly = true;
        foreach (var store in frozen.Stores)
            if (store is not null) store._isReadOnly = true;
#endif

        return frozen;
    }

    private void SubmitFromFrozen(FrozenState frozen)
    {
        // Order matches Submit and BuildDelta: Create —Hierarchy —Ops —Destroy.
        for (var i = 0; i < frozen.PendingBatchCount; i++)
        {
            if (frozen.BatchCanceled[i]) continue;
            MaterializePending(frozen.Pending, frozen.BatchEntities[i], i);
        }

        if (frozen.HierarchyByChild.Count > 0)
        {
            ApplyHierarchyToWorld(_world, frozen);
        }

        foreach (var store in frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.ApplyToWorld(_world);
        }

        for (var i = 0; i < frozen.DestroyCount; i++)
        {
            var entity = frozen.DestroyEntities[i];
            // Guard against cascade-destroyed entities (parent destroyed
            // before child in the array). Matches Replay path semantics.
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }
    }

    private static FrameDelta BuildFromFrozen(FrozenState frozen)
    {
        // Order matches Submit: Create —Hierarchy —Ops —Destroy.
        var delta = new FrameDelta();

        EmitPendingEntitiesToDelta(delta, frozen.Pending, deferredMode: false);

        EmitHierarchyToDelta(delta, frozen);

        foreach (var store in frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.EmitToDelta(delta);
        }

        for (var i = 0; i < frozen.DestroyCount; i++)
            delta.AddDestroy(frozen.DestroyEntities[i]);

        return delta;
    }

    /// <summary>
    /// Same as <see cref="BuildFromFrozen"/> but writes into an existing
    /// <see cref="FrameDelta"/> instead of allocating a new one. After warmup
    /// (buffer sized), repeated calls are zero-allocation.
    /// </summary>
    private static void BuildFromFrozenInto(FrozenState frozen, FrameDelta target)
    {
        // Order matches Submit: Create —Hierarchy —Ops —Destroy.
        target.Clear();

        EmitPendingEntitiesToDelta(target, frozen.Pending, deferredMode: false);

        EmitHierarchyToDelta(target, frozen);

        foreach (var store in frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.EmitToDelta(target);
        }

        for (var i = 0; i < frozen.DestroyCount; i++)
            target.AddDestroy(frozen.DestroyEntities[i]);
    }

    // ── Pending entity materialization ─────────────────────────────────

    private void MaterializePending(in PendingBatchView view, Entity entity, int batchIdx)
    {
        var rawCount = view.CompCounts[batchIdx];
        if (rawCount == 0)
        {
            _world.MaterializeEmptyReservedEntity(entity);
            return;
        }

        MaterializeFromBatchBuffer(entity, view.Heads[batchIdx], view.Comps, view.Buf, rawCount);
    }

    private void MaterializeFromBatchBuffer(Entity entity, int headIdx,
        BatchedComponent[] comps, byte[] buf, int rawCount)
    {
        int[]? pooledIndices = null;
        Span<int> indices = rawCount <= 64
            ? stackalloc int[rawCount]
            : (pooledIndices = ArrayPool<int>.Shared.Rent(rawCount)).AsSpan(0, rawCount);

        ComponentType[]? pooledTypes = null;
        int[]? pooledOffsets = null;
        try
        {
            var idx = DeduplicateBatchChain(comps, headIdx, indices);
            if (idx == 0)
            {
                _world.MaterializeEmptyReservedEntity(entity);
                return;
            }

            // Build (type, offset) arrays and reconstruct component mask from deduped indices
            Span<ComponentType> typesFromBatch = idx <= 64
                ? stackalloc ComponentType[idx]
                : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(idx)).AsSpan(0, idx);
            Span<int> offsets = idx <= 64
                ? stackalloc int[idx]
                : (pooledOffsets = ArrayPool<int>.Shared.Rent(idx)).AsSpan(0, idx);

            ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, b7 = 0;
            for (var i = 0; i < idx; i++)
            {
                ref var comp = ref comps[indices[i]];
                typesFromBatch[i] = comp.Type;
                offsets[i] = comp.Offset;
                TrySetBit(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, comp.Type.Value);
            }

            var mask = new ComponentMask(b0, b1, b2, b3, b4, b5, b6, b7);
            var archetype = ResolveArchetype(mask, typesFromBatch[..idx]);

            unsafe
            {
                fixed (byte* ptr = buf)
                {
                    _world.MaterializeReservedEntityRaw(entity, archetype,
                        typesFromBatch[..idx], offsets[..idx], ptr);
                }
            }
        }
        finally
        {
            if (pooledIndices is not null) ArrayPool<int>.Shared.Return(pooledIndices);
            if (pooledTypes is not null) ArrayPool<ComponentType>.Shared.Return(pooledTypes);
            if (pooledOffsets is not null) ArrayPool<int>.Shared.Return(pooledOffsets);
        }
    }

    private static void EmitPendingEntitiesToDelta(FrameDelta delta, in PendingBatchView view, bool deferredMode = false)
    {
        var batchCanceled = view.Canceled;
        var batchHeads = view.Heads;
        var batchCompCounts = view.CompCounts;
        var batchComps = view.Comps;
        var batchBuf = view.Buf;
        var batchEntities = view.Entities;
        var pendingBatchCount = view.Count;

        // Deferred (placeholder) mode: emit all Reserves first, then all
        // Creates.  This guarantees that when ReplayCore processes the delta
        // sequentially, every placeholder seq->real mapping is established
        // before any Create payload is read —essential because a Create's
        // component data may contain Entity refs that reference other
        // placeholders in the same frame.
        //
        // Real-id mode: keep the original single-pass per-entity emission
        // (Reserve + Create/Release) so that a Release of a cancelled entity
        // can recycle its id for a later Create in the same frame.
        if (deferredMode)
        {
            // Pass 1: all Reserves for non-cancelled deferred entities.
            for (var i = 0; i < pendingBatchCount; i++)
            {
                if (batchCanceled[i]) continue;
                delta.AddReserve(batchEntities[i]);
            }

            // Pass 2: all Creates for non-cancelled deferred entities.
            for (var i = 0; i < pendingBatchCount; i++)
            {
                if (batchCanceled[i]) continue;
                EmitCreateFromBatch(delta, view, i);
            }
            return;
        }

        // ── Real-id (single-pass, per-entity) ──────────────────────
        for (var i = 0; i < pendingBatchCount; i++)
        {
            var entity = batchEntities[i];
            delta.AddReserve(entity);

            if ((uint)i < (uint)batchCanceled.Length && batchCanceled[i])
            {
                delta.AddRelease(entity);
                continue;
            }

            EmitCreateFromBatch(delta, view, i);
        }
    }

    private static void EmitCreateFromBatch(FrameDelta delta, in PendingBatchView view, int i)
    {
        var batchBuf = view.Buf;
        var batchHeads = view.Heads;
        var batchCompCounts = view.CompCounts;
        var batchComps = view.Comps;

        var entity = view.Entities[i];
        var rawCount = batchCompCounts[i];
        if (rawCount == 0)
        {
            delta.AddCreate(entity, ReadOnlySpan<RawComponentValue>.Empty);
            return;
        }

        var rented = ArrayPool<RawComponentValue>.Shared.Rent(rawCount);
        var fillCount = 0;
        try
        {
            var outIdx = 0;
            var current = batchHeads[i];
            while (current >= 0)
            {
                ref var bc = ref batchComps[current];
                if (!bc.Removed)
                {
                    rented[outIdx] = ReadRawFromBuf(batchBuf, bc);
                    outIdx++;
                    fillCount = outIdx; // update in real-time so exception cleanup clears all written slots
                }
                current = bc.Next;
            }

            if (outIdx == 0)
            {
                delta.AddCreate(entity, ReadOnlySpan<RawComponentValue>.Empty);
                return;
            }

            // Sort and dedup only the filled portion.
            var comps = rented.AsSpan(0, outIdx);
            if (outIdx > 1)
                outIdx = SortAndDeduplicateComponents(comps);

            delta.AddCreate(entity, comps[..outIdx]);
        }
        finally
        {
            if (fillCount > 0)
                Array.Clear(rented, 0, fillCount);
            ArrayPool<RawComponentValue>.Shared.Return(rented);
        }
    }

    // An entity is excluded from hierarchy application when it is scheduled for
    // destruction this frame. Two sources cover all cases:
    //   1. DestroyEntities[]  — non-pending entities that had Destroy() called.
    //   2. BatchCanceled[]    — pending/placeholder entities cancelled by Destroy().
    // CancelledBatchCount provides a fast-path: when no batches were cancelled
    // (the common case), the BatchCanceled scan is skipped entirely.
    // Additionally, CancelPendingDescendants removes hierarchy entries for
    // cancelled parents at record time, so surviving entries are rare.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDestroyedThisFrame(Entity entity, FrozenState frozen)
    {
        for (var i = 0; i < frozen.DestroyCount; i++)
            if (frozen.DestroyEntities[i] == entity) return true;
        if (frozen.CancelledBatchCount == 0) return false;
        for (var i = 0; i < frozen.PendingBatchCount; i++)
            if (frozen.BatchCanceled[i] && frozen.BatchEntities[i] == entity) return true;
        return false;
    }

    private static void EmitHierarchyToDelta(FrameDelta delta, FrozenState frozen)
    {
        var hierarchyByChild = frozen.HierarchyByChild;
        if (hierarchyByChild.Count == 0) return;

        var count = hierarchyByChild.Count;
        var sorted = ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Rent(count);
        try
        {
            ((ICollection<KeyValuePair<Entity, HierarchyIntent>>)hierarchyByChild).CopyTo(sorted, 0);
            Array.Sort(sorted, 0, count, HierarchyComparer.Instance);

            for (var i = 0; i < count; i++)
            {
                ref readonly var entry = ref sorted[i];
                var (child, intent) = (entry.Key, entry.Value);
                if (IsDestroyedThisFrame(child, frozen)) continue;
                if (intent.IsAdd)
                {
                    if (IsDestroyedThisFrame(intent.Parent, frozen)) continue;
                    delta.AddAddChild(intent.Parent, child);
                }
                else
                {
                    delta.AddRemoveChild(child);
                }
            }
        }
        finally
        {
            Array.Clear(sorted, 0, count); // clear refs before returning to pool
            ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Return(sorted);
        }
    }

    private sealed class HierarchyComparer : IComparer<KeyValuePair<Entity, HierarchyIntent>>
    {
        public static readonly HierarchyComparer Instance = new();
        public int Compare(KeyValuePair<Entity, HierarchyIntent> x, KeyValuePair<Entity, HierarchyIntent> y)
            => x.Key.Id.CompareTo(y.Key.Id);
    }

    private static void ApplyHierarchyToWorld(World world, FrozenState frozen)
    {
        if (frozen.HierarchyByChild.Count == 0) return;

        var count = frozen.HierarchyByChild.Count;
        var sorted = ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Rent(count);
        try
        {
            ((ICollection<KeyValuePair<Entity, HierarchyIntent>>)frozen.HierarchyByChild).CopyTo(sorted, 0);
            Array.Sort(sorted, 0, count, HierarchyComparer.Instance);

            for (var i = 0; i < count; i++)
            {
                ref readonly var entry = ref sorted[i];
                var (child, intent) = (entry.Key, entry.Value);
                if (IsDestroyedThisFrame(child, frozen)) continue;

                if (intent.IsAdd)
                {
                    if (IsDestroyedThisFrame(intent.Parent, frozen)) continue;
                    world.AddChild(intent.Parent, child);
                }
                else
                {
                    world.RemoveChild(child);
                }
            }
        }
        finally
        {
            ArrayPool<KeyValuePair<Entity, HierarchyIntent>>.Shared.Return(sorted);
        }
    }

    // ── Pending entity helpers ────────────────────────────────────────

    private void GrowPendingBatchFor(int entityId)
    {
        if (entityId < _frozen.PendingBatch.Length) return;

        var newLen = _frozen.PendingBatch.Length == 0 ? 64 : _frozen.PendingBatch.Length;
        while (newLen <= entityId) newLen *= 2;
        var next = new int[newLen];
        Array.Fill(next, -1);
        if (_frozen.PendingBatch.Length > 0)
            Array.Copy(_frozen.PendingBatch, next, _frozen.PendingBatch.Length);
        _frozen.PendingBatch = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected bool TryGetPendingBatch(Entity entity, out int batchIdx)
    {
        if (entity == _lastCreated && _lastCreatedBatch >= 0)
        {
#if DEBUG
            if (entity.Id < 0)
            {
                var seq = entity.Version;
                if ((uint)seq >= (uint)_pendingBatchDeferredEpoch.Length ||
                    _pendingBatchDeferredEpoch[seq] != _world._deferredEpoch)
                {
                    batchIdx = -1;
                    return false;
                }
            }
#endif
            batchIdx = _lastCreatedBatch;
            return true;
        }

        if (entity.Id >= 0)
        {
            var id = entity.Id;
            if ((uint)(id - _pendingBatchMin) < (uint)(_pendingBatchMax - _pendingBatchMin) &&
                id < _frozen.PendingBatch.Length)
            {
                batchIdx = _frozen.PendingBatch[id];
                if (batchIdx >= 0 && !_frozen.BatchCanceled[batchIdx] && _frozen.BatchEntities[batchIdx] == entity)
                    return true;
            }
        }
        else
        {
            var seq = entity.Version;
            if ((uint)seq < (uint)_deferredSeq)
            {
                batchIdx = _pendingBatchDeferredArr[seq];
                if (batchIdx >= 0 && (uint)batchIdx < (uint)_frozen.BatchCanceled.Length && !_frozen.BatchCanceled[batchIdx])
                {
#if DEBUG
                    if ((uint)seq >= (uint)_pendingBatchDeferredEpoch.Length ||
                        _pendingBatchDeferredEpoch[seq] != _world._deferredEpoch)
                        return false;
#endif
                    return true;
                }
            }
        }
        batchIdx = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CancelPendingEntity(Entity entity)
    {
        if (entity == _lastCreated)
            _lastCreatedBatch = -1;

        if (entity.Id >= 0)
        {
            var id = entity.Id;
            if (id < _frozen.PendingBatch.Length)
            {
                var batchIdx = _frozen.PendingBatch[id];
                if (batchIdx >= 0)
                {
                    _world.ReleaseReservedEntity(entity);
                    _frozen.PendingBatch[id] = -1;
                    _frozen.BatchHeads[batchIdx] = -1;
                    _frozen.BatchCompCounts[batchIdx] = 0;
                    _frozen.BatchCanceled[batchIdx] = true;
                    _frozen.CancelledBatchCount++;
                    _frozen.HierarchyByChild.Remove(entity);
                }
            }
        }
        else
        {
            var seq = entity.Version;
            if ((uint)seq < (uint)_deferredSeq)
            {
                var batchIdx = _pendingBatchDeferredArr[seq];
                if (batchIdx >= 0)
                {
                    _pendingBatchDeferredArr[seq] = -1;
                    _frozen.BatchHeads[batchIdx] = -1;
                    _frozen.BatchCompCounts[batchIdx] = 0;
                    _frozen.BatchCanceled[batchIdx] = true;
                    _frozen.CancelledBatchCount++;
                    _frozen.HierarchyByChild.Remove(entity);
                }
            }
        }
    }

    // ── Batch buffer helpers ──────────────────────────────────────────

    // When a pending entity is destroyed before Submit, all hierarchy entries
    // referencing it as a parent must be cleaned up — the entity will never be
    // materialized. Pending children are cancelled recursively; existing children
    // simply have their AddChild intent removed.
    private void CancelPendingDescendants(Entity root)
    {
        if (_frozen.HierarchyByChild.Count == 0) return;

        // BFS through ALL descendants. We must snapshot children before
        // calling CancelPendingEntity because that mutates _frozen.HierarchyByChild.
        var queue = ArrayPool<Entity>.Shared.Rent(16);
        var queueCount = 0;
        try
        {
            EnqueueAllChildren(root, ref queue, ref queueCount);

            var head = 0;
            while (head < queueCount)
            {
                var current = queue[head++];
                if (TryGetPendingBatch(current, out _))
                {
                    CancelPendingEntity(current);
                    EnqueueAllChildren(current, ref queue, ref queueCount);
                }
                else
                {
                    // Existing child of a cancelled pending parent: remove the
                    // AddChild intent since the parent will never materialize.
                    _frozen.HierarchyByChild.Remove(current);
                }
            }
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(queue);
        }
    }

    private void EnqueueAllChildren(Entity parent, ref Entity[] queue, ref int queueCount)
    {
        foreach (var (child, intent) in _frozen.HierarchyByChild)
        {
            if (!intent.IsAdd || intent.Parent != parent) continue;
            if (queueCount == queue.Length)
                GrowPooled(ref queue, queueCount);
            queue[queueCount++] = child;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void WritePendingComponent<T>(int batchIdx, T component) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var offset = ReserveBatchBufSpace(size);
        Unsafe.WriteUnaligned(ref _frozen.BatchBuf[offset], component);
        CommitBatchComponent(batchIdx, CommandTypeInfo<T>.Type, offset, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected int ReserveBatchBufSpace(int size)
    {
        if (_batchBufLen + size > _frozen.BatchBuf.Length)
            Array.Resize(ref _frozen.BatchBuf, Math.Max(_batchBufLen + size, _frozen.BatchBuf.Length == 0 ? 4096 : _frozen.BatchBuf.Length * 2));
        var offset = _batchBufLen;
        _batchBufLen += size;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CommitBatchComponent(int batchIdx, ComponentType type, int offset, int size)
    {
        EnsureCapacity(ref _frozen.BatchComps, _batchCompTotal, 256);
        _frozen.BatchComps[_batchCompTotal] = new BatchedComponent
        {
            Type = type,
            Offset = offset,
            Size = size,
            Next = _frozen.BatchHeads[batchIdx],
        };
        _frozen.BatchHeads[batchIdx] = _batchCompTotal;
        _batchCompTotal++;
        _frozen.BatchCompCounts[batchIdx]++;
    }

    private protected void MarkBatchComponentRemoved(int batchIdx, ComponentType targetType)
    {
        var current = _frozen.BatchHeads[batchIdx];
        while (current >= 0)
        {
            ref var comp = ref _frozen.BatchComps[current];
            if (comp.Type == targetType)
                comp.Removed = true;
            current = comp.Next;
        }
    }

    // ── Archetype resolution ──────────────────────────────────────────

    /// <summary>
    /// Resolves an Archetype for a mask+types pair.
    /// Canonical masks (all IDs &lt; 512) use a small local cache
    /// (1-slot last + 8-slot LRU) on top of the World-level unified resolver.
    /// Non-canonical masks bypass the local cache (they are not unique keys
    /// for the mask alone) and go directly to <see cref="World.GetOrCreateArchetype(ComponentMask, ReadOnlySpan{ComponentType})"/>.
    /// </summary>
    /// <remarks>
    /// <b>Synchronization note:</b> <c>_maskCache</c>, <c>_lastMask</c>, and
    /// <c>_lastMaskArchetype</c> are intentionally unsynchronized.
    /// This method is only called from the Submit/materialize path
    /// (<see cref="MaterializeFromBatchBuffer"/>), which is single-threaded
    /// per <c>World</c>'s threading contract — <see cref="ParallelCommandStream"/>
    /// covers the recording phase (append-only), not Submit.
    /// If parallel materialization is ever added, the correct fix is to give
    /// each worker its own local mask cache + serialize archetype creation,
    /// NOT a lock around this cache.
    /// </remarks>
    private Archetype ResolveArchetype(ComponentMask mask, scoped ReadOnlySpan<ComponentType> types)
    {
        // Non-canonical: bypass local cache (mask is not a unique key).
        if (!World.IsMaskCanonical(mask, types.Length))
            return _world.GetOrCreateArchetype(mask, types);

        // Zero-component mask: empty signature.
        if (mask.IsZero())
            return _world.GetOrCreateArchetype(Signature.Empty);

        // ── Canonical mask: use local cache ──
        var generation = _world.ArchetypeCacheGeneration;
        if (_maskCacheGeneration != generation)
        {
            _maskCacheGeneration = generation;
            _maskCacheCount = 0;
            _lastMaskArchetype = null;
        }

        if (_lastMaskArchetype is { } last && _lastMask.Equals(mask))
            return last;

        for (var i = 0; i < _maskCacheCount; i++)
        {
            ref var slot = ref _maskCache[i];
            if (slot.Mask.Equals(mask))
            {
                _lastMask = mask;
                _lastMaskArchetype = slot.Archetype;
                return slot.Archetype;
            }
        }

        // Cache miss: use World's unified resolver, then cache locally.
        var archetype = _world.GetOrCreateArchetype(mask, types);

        if (_maskCache.Length == 0)
            _maskCache = new MaskCacheSlot[MaskCacheSize];

        var slotIdx = _maskCacheCount < MaskCacheSize
            ? _maskCacheCount++
            : mask.GetHashCode() & (MaskCacheSize - 1);

        _maskCache[slotIdx] = new MaskCacheSlot(mask, archetype);
        _lastMask = mask;
        _lastMaskArchetype = archetype;
        return archetype;
    }

    // ── Bit helpers ───────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySetBit(ref ulong b0, ref ulong b1, ref ulong b2, ref ulong b3,
                                  ref ulong b4, ref ulong b5, ref ulong b6, ref ulong b7, int id)
    {
        if (id < 64)      return TrySetBitInLane(ref b0, id);
        if (id < 128)     return TrySetBitInLane(ref b1, id - 64);
        if (id < 192)     return TrySetBitInLane(ref b2, id - 128);
        if (id < 256)     return TrySetBitInLane(ref b3, id - 192);
        if (id < 320)     return TrySetBitInLane(ref b4, id - 256);
        if (id < 384)     return TrySetBitInLane(ref b5, id - 320);
        if (id < 448)     return TrySetBitInLane(ref b6, id - 384);
        return TrySetBitInLane(ref b7, id - 448);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySetBitInLane(ref ulong lane, int bitIndex)
    {
        var bit = 1UL << bitIndex;
        if ((lane & bit) != 0) return false;
        lane |= bit;
        return true;
    }

    // ── Clone helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Writes the virtual children of <paramref name="parent"/> into <paramref name="buffer"/>
    /// and returns the count. Virtual children = world hierarchy children + pending AddChild
    /// intents - pending RemoveChild intents.
    /// </summary>
    private int GetVirtualChildren(Entity parent, Span<Entity> buffer)
    {
        var count = 0;

        // 1. World real children
        if (_world.Hierarchy.HasChildren(_world, parent))
        {
            foreach (var child in _world.Hierarchy.EnumerateChildren(_world, parent))
            {
                if (count < buffer.Length)
                    buffer[count++] = child;
            }
        }

        // 2. Pending HierarchyByChild intents: AddChild (if Parent matches) then RemoveChild
        //    in a single pass. RemoveChild has no Parent filter — see note below.
        foreach (var (child, intent) in _frozen.HierarchyByChild)
        {
            if (intent.IsAdd)
            {
                if (intent.Parent == parent)
                {
                    // Add if not already present (world children may already include it)
                    var alreadyPresent = false;
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] == child) { alreadyPresent = true; break; }
                    }
                    if (!alreadyPresent && count < buffer.Length)
                        buffer[count++] = child;
                }
            }
            else
            {
                // NOTE: No intent.Parent == parent check here. In a single-parent hierarchy,
                // RemoveChild always unlinks the child from its current parent regardless of
                // which parent GetVirtualChildren is querying for. RemoveChildCore stores
                // default(Entity) as Parent, so checking intent.Parent would be incorrect
                // (it would never match any real parent and RemoveChild would be a no-op).
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == child)
                    {
                        // Shift remaining elements left
                        for (var j = i; j < count - 1; j++)
                            buffer[j] = buffer[j + 1];
                        count--;
                        break;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Clones all children of <paramref name="sourceRoot"/> under <paramref name="cloneRoot"/>
    /// using the virtual hierarchy view (world children + pending AddChild intents − pending
    /// RemoveChild intents). Iterative DFS with explicit stack — not recursive.
    /// </summary>
    private void CloneChildrenFromVirtualHierarchy(Entity sourceRoot, Entity cloneRoot)
    {
        // Use ArrayPool buffer for virtual children (replaces heap List)
        var childBuf = ArrayPool<Entity>.Shared.Rent(32);
        var childCount = GetVirtualChildren(sourceRoot, childBuf.AsSpan());
        if (childCount == 0)
        {
            ArrayPool<Entity>.Shared.Return(childBuf);
            return;
        }

        // Cycle detection: visited set using ArrayPool + linear scan (typical pending entities < 64)
        var visited = ArrayPool<Entity>.Shared.Rent(32);
        var visitedCount = 0;

        var stack = ArrayPool<Entity>.Shared.Rent(32);
        var cloneStack = ArrayPool<Entity>.Shared.Rent(32);
        var stackCount = 0;

        try
        {
            // Buffer for grandchildren (reused across iterations)
            Span<Entity> gcBuf = stackalloc Entity[32];

            for (var ci = 0; ci < childCount; ci++)
            {
                var child = childBuf[ci];

                // Cycle check via linear scan
                if (Contains(visited, visitedCount, child))
                    throw new InvalidOperationException(
                        $"Clone detected a cycle in the virtual hierarchy at entity {child}.");
                visited[visitedCount++] = child;

                if (stackCount >= stack.Length)
                {
                    GrowPooled(ref stack, stackCount);
                    GrowPooled(ref cloneStack, stackCount);
                }
                stack[stackCount] = child;
                cloneStack[stackCount] = cloneRoot;
                stackCount++;
            }

            // Return child buffer early since we've pushed all entries
            ArrayPool<Entity>.Shared.Return(childBuf);
            childBuf = null!;

            while (stackCount > 0)
            {
                stackCount--;
                var srcChild = stack[stackCount];
                var cloneParent = cloneStack[stackCount];

                var cloneChild = CreateCore();
                int batchIdx;
                if (_deferredEntities)
                {
                    // CreateCore (via CreateDeferredImpl) always allocates a batch slot
                    // and sets _lastCreated, so TryGetPendingBatch must succeed. Check
                    // the return value explicitly as a defensive invariant guard.
                    if (!TryGetPendingBatch(cloneChild, out batchIdx))
                    {
                        throw new InvalidOperationException(
                            $"Clone: deferred clone child {cloneChild} has no pending batch slot.");
                    }
                }
                else
                    batchIdx = _frozen.PendingBatch[cloneChild.Id];

                // Check if child is a pending entity (same buffer)
                if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(srcChild, out var srcChildBatchIdx))
                {
                    // Copy components from pending source batch
                    CopyComponentsFromBatch(srcChild, srcChildBatchIdx, cloneChild, batchIdx);
                }
                else
                {
                    // Materialized child: read archetype + overlay scan (same as root)
                    if (!_world.TryGetLocation(srcChild, out var childLocation))
                        throw new InvalidOperationException(
                            $"Clone failed: child entity {srcChild} has no location. " +
                            "The source entity may be corrupted.");
                    CloneMaterializedComponents(srcChild, childLocation, batchIdx);
                }
                AddChildCore(cloneParent, cloneChild);

                // Enqueue grandchildren (virtual view)
                var gcCount = GetVirtualChildren(srcChild, gcBuf);
                for (var gi = 0; gi < gcCount; gi++)
                {
                    var grandChild = gcBuf[gi];

                    // Cycle check via linear scan
                    if (Contains(visited, visitedCount, grandChild))
                        throw new InvalidOperationException(
                            $"Clone detected a cycle in the virtual hierarchy at entity {grandChild}.");
                    visited[visitedCount++] = grandChild;

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
            if (childBuf is not null)
                ArrayPool<Entity>.Shared.Return(childBuf);
            ArrayPool<Entity>.Shared.Return(visited);
            ArrayPool<Entity>.Shared.Return(stack);
            ArrayPool<Entity>.Shared.Return(cloneStack);
        }
    }

    /// <summary>Linear scan for entity in span (allocation-free).</summary>
    private static bool Contains(Span<Entity> span, int count, Entity entity)
    {
        for (var i = 0; i < count; i++)
            if (span[i] == entity) return true;
        return false;
    }

    /// <summary>
    /// Walks a batch linked-list chain with last-wins deduplication.
    /// Writes indices of non-removed, deduplicated <see cref="BatchedComponent"/> entries
    /// into <paramref name="indices"/> and returns the count.
    /// The caller must ensure <paramref name="indices"/> is large enough (upper bound is
    /// the raw component count for this batch — deduplication can only reduce it).
    /// Shared by <see cref="MaterializeFromBatchBuffer"/> and <see cref="CopyComponentsFromBatch"/>.
    /// </summary>
    private static int DeduplicateBatchChain(
        BatchedComponent[] comps, int headIdx,
        Span<int> indices)
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, b7 = 0;
        var idx = 0;
        var current = headIdx;
        while (current >= 0)
        {
            ref var comp = ref comps[current];
            if (!comp.Removed)
            {
                var id = comp.Type.Value;
                if (id < 512)
                {
                    if (TrySetBit(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, id))
                    {
                        indices[idx] = current;
                        idx++;
                    }
                }
                else
                {
                    var seen = false;
                    for (var j = 0; j < idx; j++)
                    {
                        if (comps[indices[j]].Type.Value == id) { seen = true; break; }
                    }
                    if (!seen)
                    {
                        indices[idx] = current;
                        idx++;
                    }
                }
            }
            current = comp.Next;
        }
        return idx;
    }

    private static int SortAndDeduplicateComponents(Span<RawComponentValue> comps)
    {
        for (var i = 1; i < comps.Length; i++)
        {
            var key = comps[i];
            var j = i - 1;
            while (j >= 0 && comps[j].ComponentType.Value > key.ComponentType.Value)
            {
                comps[j + 1] = comps[j];
                j--;
            }
            comps[j + 1] = key;
        }

        var writeIdx = 0;
        for (var readIdx = 0; readIdx < comps.Length; readIdx++)
        {
            if (writeIdx == 0 || comps[readIdx].ComponentType != comps[writeIdx - 1].ComponentType)
            {
                if (writeIdx != readIdx)
                    comps[writeIdx] = comps[readIdx];
                writeIdx++;
            }
        }
        return writeIdx;
    }

    // ── Destroy helpers ───────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendDestroy(Entity entity)
    {
        EnsureCapacity(ref _frozen.DestroyEntities, _frozen.DestroyCount, 64);
        _frozen.DestroyEntities[_frozen.DestroyCount++] = entity;
    }

    // ── Store management ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ComponentStore<T> GetOrCreateStore<T>() where T : unmanaged
    {
        var id = CommandTypeInfo<T>.Type.Value;
        Debug.Assert(id >= 0, "Component type id must be non-negative.");

        // Dirty: caller will write to this store. Must be set before any cache-hit return
        // because the cache survives Clear() —a post-Clear cache hit still dirties the stream.
        // Conditional write avoids repeated store-buffer / cache-line pressure in the hot path.
        if (!_hasStoreCommands) _hasStoreCommands = true;

        // 2-slot LRU cache: avoids Stores array lookups for repeated/alternating types.
        if (_lastStoreId0 == id)
            return Unsafe.As<ComponentStore<T>>(_lastStore0!);
        if (_lastStoreId1 == id)
        {
            // Promote slot1 to most-recently-used.
            (_lastStoreId0, _lastStoreId1) = (_lastStoreId1, _lastStoreId0);
            (_lastStore0, _lastStore1) = (_lastStore1, _lastStore0);
            return Unsafe.As<ComponentStore<T>>(_lastStore0!);
        }

        var stores = _frozen.Stores;
        if ((uint)id >= (uint)stores.Length)
        {
            var newLen = Math.Max(id + 1, stores.Length == 0 ? 16 : stores.Length * 2);
            Array.Resize(ref _frozen.Stores, newLen);
            stores = _frozen.Stores;
        }

        ref var slot = ref MemoryMarshal.GetArrayDataReference(stores);
        slot = ref Unsafe.Add(ref slot, id);
        var store = slot;
        if (store is null)
        {
            store = new ComponentStore<T>();
            slot = store;
        }

        Debug.Assert(store is ComponentStore<T>,
            $"Slot {id} holds {store.GetType()} but expected ComponentStore<{typeof(T)}>.");

        // Insert into LRU cache (evict least-recently-used).
        _lastStoreId1 = _lastStoreId0; _lastStore1 = _lastStore0;
        _lastStoreId0 = id; _lastStore0 = store;

        return Unsafe.As<ComponentStore<T>>(store);
    }

    private protected ComponentStore<T> GetOrCreateStoreParallel<T>() where T : unmanaged
    {
        var id = CommandTypeInfo<T>.Type.Value;

        // Dirty: concurrent writer will AppendConcurrent to this store.
        // _hasParallelStoreWrites ensures SealParallelStores() iterates stores to
        // merge the per-thread local buffers before applying/snapshotting.
        // Conditional writes avoid repeated store-buffer / cache-line pressure in the hot path.
        if (!_hasStoreCommands) _hasStoreCommands = true;
        if (!_hasParallelStoreWrites) _hasParallelStoreWrites = true;
        if (id >= _frozen.Stores.Length)
        {
            lock (_storeCreateLock)
            {
                if (id >= _frozen.Stores.Length)
                {
                    var newLen = Math.Max(id + 1, _frozen.Stores.Length == 0 ? 16 : _frozen.Stores.Length * 2);
                    Array.Resize(ref _frozen.Stores, newLen);
                }
            }
        }

        var store = _frozen.Stores[id];
        if (store is null)
        {
            lock (_storeCreateLock)
            {
                store = _frozen.Stores[id];
                if (store is null)
                {
                    store = new ComponentStore<T>();
                    _frozen.Stores[id] = store;
                }
            }
        }
        return (ComponentStore<T>)store;
    }

    private int _deferredSeq;
    private int[] _pendingBatchDeferredArr = [];
    // Indexed by placeholder.Version (== seq), value is the resolved real Entity.
    // Entries outside [0, _deferredSeq) are stale; entries inside default to
    // InvalidReal and are written with real entities as they get allocated.
    // Id < 0 means "not yet resolved" (cancelled placeholder); real ids are >= 0.
    private Entity[]? _resolveMapPool;
#if DEBUG
    private int[] _pendingBatchDeferredEpoch = [];
#endif

    // ── Clear ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resets the stream to its initial state, discarding all recorded commands
    /// without applying them to the World. Reserved entity ids allocated by
    /// deferred Create or by Snapshot are released back to the World's free list.
    ///
    /// Use this for relay-only scenarios where recorded commands are forwarded as
    /// a FrameDelta (via <see cref="Snapshot"/>) but should not run locally.
    /// For the normal "record and apply" flow, prefer <see cref="Submit"/>,
    /// which materializes entities into the World and then clears.
    /// </summary>
    public void Clear()
    {
        Clear(releaseReserved: true);
    }

    private void Clear(bool releaseReserved)
    {
        foreach (var store in _frozen.Stores)
            store?.Clear();

        // Submit path: every non-cancelled batch entity has been materialized to
        // alive by MaterializeAllPending, so TryReleaseReserved returns false and
        // we just drop the pending-batch index.
        // Snapshot/relay path (or Submit exception path): batch entities may still
        // be in the reserved state, so we release their ids back to the World's
        // free list here. Either way Clear is self-sufficient —it does not rely
        // on the caller having materialized anything.
        for (var i = 0; i < _frozen.PendingBatchCount; i++)
        {
            var entity = _frozen.BatchEntities[i];
            if (entity.Id < 0) continue;
            _frozen.PendingBatch[entity.Id] = -1;
            if (_frozen.BatchCanceled[i])
            {
                // Cancelled batch: release the reserved entity back to the free list.
                // Non-cancelled entities are already materialized (Submit path) and
                // TryReleaseReserved returns false; cancelled ones were never
                // materialized and must be released to keep the free list in sync
                // with ReplayCore's Release operation.
                _world.TryReleaseReserved(entity);
                continue;
            }
            if (releaseReserved) _world.TryReleaseReserved(entity);
        }
        _deferredSeq = 0;
        if (_pendingReplay)
        {
            // Snapshot was called before Clear —preserve tracked registrations
            // so Replay() can resolve them after the underlying ReplayCore call.
            _replayTrackedBySeq = _trackedBySeq;
            _replayTrackedMaxSeq = _trackedMaxSeq;
            _pendingReplay = false;
        }
        else
        {
            // No pending replay: discard any stale saved registrations and
            // the active tracked slots. This prevents abandoned-frame slots
            // from being resolved by a future Replay() call.
            _replayTrackedBySeq = null;
            _replayTrackedMaxSeq = 0;
        }
        _trackedBySeq = [];
        _trackedMaxSeq = 0;
        _frozen.DestroyCount = 0;
        _frozen.PendingBatchCount = 0;
        _frozen.CancelledBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
        _batchBufLen = 0;
        _lastCreated = default;
        _lastCreatedBatch = -1;
        _frozen.HierarchyByChild.Clear();
        _hasStoreCommands = false;
        _hasParallelStoreWrites = false;
#if DEBUG
        _pendingBatchDeferredEpoch = [];
        _world._deferredEpoch++;
#endif
    }

    // ── Deferred entity resolution ────────────────────────────────────

    // ── EntitySlot resolution ────────────────────────────────────────

    /// <summary>
    /// Resolves all tracked slots using a Submit-path resolve map.
    /// Called at the end of <see cref="ResolveDeferredCreates"/>.
    /// </summary>
    private void ResolveTrackedSlots(Entity[] resolveMap, int mapLen)
    {
        if (_trackedMaxSeq == 0) return;

        var max = Math.Min(_trackedMaxSeq, _trackedBySeq.Length);
        for (var seq = 0; seq < max; seq++)
        {
            var s = _trackedBySeq[seq];
            if (s is null) continue;

            var hasReal = (uint)seq < (uint)mapLen && resolveMap[seq].Id >= 0;

            while (s is not null)
            {
                var next = s.Next;
                if (hasReal)
                    s.Entity = resolveMap[seq];
                s.Next = null;  // break chain so Slot doesn't retain linked list
                s = next;
            }

            _trackedBySeq[seq] = null;
        }

        _trackedMaxSeq = 0;
    }

    /// <summary>
    /// Resolves all tracked slots using the World's replay placeholder map.
    /// Called after <see cref="Replay(FrameDelta, Boolean)"/> when the
    /// <c>resolveSlots</c> parameter is <c>true</c>.
    /// </summary>
    private void ResolveTrackedSlotsFromReplay()
    {
        var slots = _replayTrackedBySeq;
        var maxSeq = _replayTrackedMaxSeq;
        _replayTrackedBySeq = null;
        _replayTrackedMaxSeq = 0;

        if (maxSeq == 0) return;

        var max = Math.Min(maxSeq, slots!.Length);
        for (var seq = 0; seq < max; seq++)
        {
            var s = slots[seq];
            if (s is null) continue;

            if (_world.TryResolvePlaceholder(new Entity(-1, seq), out var real))
            {
                while (s is not null)
                {
                    var next = s.Next;
                    s.Entity = real;
                    s.Next = null;
                    s = next;
                }
            }

            slots[seq] = null;
        }
    }

    private void ResolveDeferredCreates()
    {
        // Discard any pending-replay registrations —they belong to a
        // previous Snapshot+Clear cycle that was not followed by Replay.
        _replayTrackedBySeq = null;
        _replayTrackedMaxSeq = 0;

        if (_deferredSeq == 0)
            return;

        var resolveMap = _resolveMapPool ?? [];
        _resolveMapPool = null;
        EnsureCapacity(ref resolveMap, _deferredSeq - 1, 64);
        Array.Fill(resolveMap, new Entity(-1, -1)); // IsUnmappedSentinel —fill entire array to wipe stale pool data

        for (var seq = 0; seq < _deferredSeq; seq++)
        {
            var batchIdx = _pendingBatchDeferredArr[seq];
            _pendingBatchDeferredArr[seq] = -1;
            if (batchIdx < 0)
                continue;
            if ((uint)batchIdx < (uint)_frozen.BatchCanceled.Length && _frozen.BatchCanceled[batchIdx])
                continue;

            var real = _world.ReserveDeferredEntityUnsafe();
            resolveMap[seq] = real;

            _frozen.BatchEntities[batchIdx] = real;

            GrowPendingBatchFor(real.Id);
            _frozen.PendingBatch[real.Id] = batchIdx;
            if (real.Id < _pendingBatchMin) _pendingBatchMin = real.Id;
            if (real.Id >= _pendingBatchMax) _pendingBatchMax = real.Id + 1;
        }

        _deferredSeq = 0;
#if DEBUG
        _pendingBatchDeferredEpoch = [];
#endif

        foreach (var store in _frozen.Stores)
            store?.ReplacePlaceholders(resolveMap);

        ReplaceHierarchyPlaceholders(resolveMap);

        // Resolve embedded Entity refs in pending-batch (created-entity) component data.
        var resolveSpan = new ReadOnlySpan<Entity>(resolveMap);
        for (var i = 0; i < _frozen.PendingBatchCount; i++)
        {
            if (_frozen.BatchCanceled[i]) continue;
            var current = _frozen.BatchHeads[i];
            while (current >= 0)
            {
                ref var bc = ref _frozen.BatchComps[current];
                if (!bc.Removed)
                {
                    EntityFieldResolver.ResolveInPlace(
                        new Span<byte>(_frozen.BatchBuf, bc.Offset, bc.Size),
                        bc.Type, resolveSpan);
                }
                current = bc.Next;
            }
        }

        for (var i = 0; i < _frozen.DestroyCount; i++)
        {
            ref var destroyed = ref _frozen.DestroyEntities[i];
            if (destroyed.IsPlaceholder)
            {
                var real = resolveMap[destroyed.Version];
                if (real.Id >= 0) destroyed = real;
            }
        }
        _resolveMapPool = resolveMap;

        // Resolve tracked EntitySlots using this resolve map.
        // _deferredSeq was reset to 0 above, so use resolveMap.Length as the bound.
        ResolveTrackedSlots(resolveMap, resolveMap.Length);
    }

    private void ReplaceHierarchyPlaceholders(Entity[] resolveMap)
    {
        if (_frozen.HierarchyByChild.Count == 0)
            return;

        var count = _frozen.HierarchyByChild.Count;
        var replacements = ArrayPool<HierarchyReplacement>.Shared.Rent(count);
        var repCount = 0;
        try
        {
            foreach (var (child, intent) in _frozen.HierarchyByChild)
            {
                var newChild = child;
                if (child.IsPlaceholder)
                {
                    var resolved = resolveMap[child.Version];
                    if (resolved.Id >= 0) newChild = resolved;
                }
                var newParent = intent.Parent;
                if (intent.IsAdd && intent.Parent.IsPlaceholder)
                {
                    var resolved = resolveMap[intent.Parent.Version];
                    if (resolved.Id >= 0) newParent = resolved;
                }

                if (newChild != child || (intent.IsAdd && newParent != intent.Parent))
                {
                    replacements[repCount++] = new HierarchyReplacement(
                        child, newChild, intent.IsAdd, newParent);
                }
            }

            for (var i = 0; i < repCount; i++)
            {
                ref var r = ref replacements[i];
                _frozen.HierarchyByChild.Remove(r.OldChild);
                if (r.IsAdd)
                    _frozen.HierarchyByChild[r.NewChild] = new HierarchyIntent(true, r.Parent);
                else
                    _frozen.HierarchyByChild[r.NewChild] = new HierarchyIntent(false, default);
            }
        }
        finally
        {
            ArrayPool<HierarchyReplacement>.Shared.Return(replacements);
        }
    }

    private struct HierarchyReplacement
    {
        public Entity OldChild;
        public Entity NewChild;
        public bool IsAdd;
        public Entity Parent;

        public HierarchyReplacement(Entity oldChild, Entity newChild, bool isAdd, Entity parent)
        {
            OldChild = oldChild;
            NewChild = newChild;
            IsAdd = isAdd;
            Parent = parent;
        }
    }

    internal object? ActiveHierarchyForTesting => _frozen.HierarchyByChild;
    internal object? ActiveFrozenForTesting => _pendingFrozen;

    // ── Helpers ───────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureCapacity<T>(ref T[] array, int count, int defaultSize = 16)
    {
        if (count < array.Length) return;
        var newLen = array.Length == 0 ? defaultSize : array.Length * 2;
        while (count >= newLen) newLen *= 2;
        Array.Resize(ref array, newLen);
    }

    private static RawComponentValue ReadRawFromBuf(byte[] buf, in BatchedComponent bc)
    {
        return new RawComponentValue(bc.Type, buf, bc.Offset, bc.Size);
    }

    private protected static class CommandTypeInfo<T> where T : unmanaged
    {
        public static readonly ComponentType Type = Component<T>.ComponentType;
    }

    // ── Internal types ────────────────────────────────────────────────

    private protected const byte KindAdd = 0;
    private protected const byte KindSet = 1;
    private protected const byte KindRemove = 2;

    /// <summary>
    /// Merges component store overlay entries directly into a flat array during Clone,
    /// eliminating the intermediate <see cref="List{T}"/> allocation of <c>OverlayCollector</c>.
    /// <para/>
    /// Callbacks from <see cref="ComponentStore.ForEachEntityEntry"/> go straight into
    /// the caller's stackalloc (or pooled) arrays — no lambda closures, no temp lists.
    /// </summary>
    private protected ref struct ComponentMerger
    {
        private readonly CommandStreamCore _core;
        private Span<ComponentType> _types;
        private Span<int> _offsets;
        private Span<int> _sizes;
        private ref int _count;
        private ComponentType[]? _rentedTypes;
        private int[]? _rentedOffsets;
        private int[]? _rentedSizes;

        public ComponentMerger(CommandStreamCore core,
            Span<ComponentType> types, Span<int> offsets, Span<int> sizes,
            ref int count)
        {
            _core = core;
            _types = types;
            _offsets = offsets;
            _sizes = sizes;
            _count = ref count;
            _rentedTypes = null;
            _rentedOffsets = null;
            _rentedSizes = null;
        }

        public readonly Span<ComponentType> Types => _types;
        public readonly Span<int> Offsets => _offsets;
        public readonly Span<int> Sizes => _sizes;
        public readonly int Count => _count;

        /// <summary>Returns any rented arrays back to the pool. Safe to call even if no arrays were rented.</summary>
        public void ReturnRented()
        {
            if (_rentedTypes is not null)
            {
                ArrayPool<ComponentType>.Shared.Return(_rentedTypes);
                _rentedTypes = null;
            }
            if (_rentedOffsets is not null)
            {
                ArrayPool<int>.Shared.Return(_rentedOffsets);
                _rentedOffsets = null;
            }
            if (_rentedSizes is not null)
            {
                ArrayPool<int>.Shared.Return(_rentedSizes);
                _rentedSizes = null;
            }
        }

        /// <summary>
        /// Merges one overlay entry directly into the typed arrays.
        /// <c>KindRemove</c> → find and shift-delete from the arrays.
        /// <c>KindAdd</c> / <c>KindSet</c> → overwrite in place or append.
        /// </summary>
        public void Add(ComponentType type, byte kind, ReadOnlySpan<byte> data)
        {
            if (kind == KindRemove)
            {
                // KindRemove: find type and remove by shifting remaining elements left
                for (var i = 0; i < _count; i++)
                {
                    if (_types[i] == type)
                    {
                        var shiftCount = _count - i - 1;
                        for (var j = 0; j < shiftCount; j++)
                        {
                            _types[i + j] = _types[i + j + 1];
                            _offsets[i + j] = _offsets[i + j + 1];
                            _sizes[i + j] = _sizes[i + j + 1];
                        }
                        _count--;
                        return;
                    }
                }
                // Type not found → nothing to remove
                return;
            }

            // KindAdd / KindSet: ensure capacity for at least one more entry
            if (_count >= _types.Length)
                Grow();

            // Reserve batch buffer space and copy data
            var offset = _core.ReserveBatchBufSpace(data.Length);
            data.CopyTo(new Span<byte>(_core._frozen.BatchBuf, offset, data.Length));

            // Find existing type → overwrite; otherwise append
            for (var i = 0; i < _count; i++)
            {
                if (_types[i] == type)
                {
                    _offsets[i] = offset;
                    _sizes[i] = data.Length;
                    return;
                }
            }

            // Append new entry
            _types[_count] = type;
            _offsets[_count] = offset;
            _sizes[_count] = data.Length;
            _count++;
        }

        private void Grow()
        {
            var newLen = _types.Length == 0 ? 64 : _types.Length * 2;

            var newTypes = ArrayPool<ComponentType>.Shared.Rent(newLen);
            var newOffsets = ArrayPool<int>.Shared.Rent(newLen);
            var newSizes = ArrayPool<int>.Shared.Rent(newLen);

            _types[.._count].CopyTo(newTypes);
            _offsets[.._count].CopyTo(newOffsets);
            _sizes[.._count].CopyTo(newSizes);

            ReturnRented();

            _types = newTypes;
            _offsets = newOffsets;
            _sizes = newSizes;
            _rentedTypes = newTypes;
            _rentedOffsets = newOffsets;
            _rentedSizes = newSizes;
        }
    }

    private protected abstract class ComponentStore
    {
        public abstract bool HasCommands { get; }
        public abstract void ApplyToWorld(World world);
        public abstract void EmitToDelta(FrameDelta delta);
        public abstract void PruneStaleCommands(World world);
        public abstract void Clear();
        public abstract void ReplacePlaceholders(Entity[] resolveMap);
        public abstract void SealParallelWrites();
        protected internal abstract void ForEachEntityEntry(Entity entity, ref ComponentMerger merger);

#if DEBUG
        internal bool _isReadOnly;
#endif
    }

    private struct StoreEntry<T> where T : unmanaged
    {
        public Entity Entity;
        public byte Kind;
        public T Value;
    }

    private protected sealed class ComponentStore<T> : ComponentStore where T : unmanaged
    {
        // ── Main (merged) storage — read path ──
        private StoreEntry<T>[] _entries = [];
        private int _count;

        // ── Kind tracking: enables a branchless Set-only fast path in ApplyToWorld ──
        // _allSetKind is true only when every entry in this store has Kind == KindSet.
        private bool _allSetKind = true;

        // ── Per-thread local buffers — write path (parallel recording) ──
        private sealed class LocalBuffer
        {
            public StoreEntry<T>[] Entries = new StoreEntry<T>[256];
            public int Count;

            /// <summary>Append one entry. Returns true if this was the first entry (buffer was empty).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Append(Entity entity, in T value, byte kind)
            {
                var i = Count;
                if ((uint)i >= (uint)Entries.Length)
                {
                    var newLen = Entries.Length == 0 ? 256 : Entries.Length * 2;
                    Array.Resize(ref Entries, newLen);
                }

                Entries[i] = new StoreEntry<T>
                {
                    Entity = entity,
                    Kind = kind,
                    Value = value,
                };
                Count = i + 1;
                return i == 0;
            }
        }

        // ── ThreadLocal storage (used for enumeration) ──
        private readonly ThreadLocal<LocalBuffer> _locals =
            new(() => new LocalBuffer(), trackAllValues: true);

        // ── [ThreadStatic] front-cache: avoids ThreadLocal.Value lookup on hot path ──
        private static int s_nextCacheId;
        private readonly int _cacheId = Interlocked.Increment(ref s_nextCacheId);

        [ThreadStatic] private static int t_cachedStoreId;
        [ThreadStatic] private static LocalBuffer? t_cachedLocal;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LocalBuffer GetLocal()
        {
            if (t_cachedStoreId == _cacheId)
            {
                var local = t_cachedLocal;
                if (local is not null) return local;
            }
            return GetLocalSlow();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private LocalBuffer GetLocalSlow()
        {
            var local = _locals.Value!;
            t_cachedStoreId = _cacheId;
            t_cachedLocal = local;
            return local;
        }

        private volatile int _hasLocalWrites;

        // ── Public API ──

        public override bool HasCommands => _count > 0 || _hasLocalWrites != 0;

        public void Append(Entity entity, in T value, byte kind)
        {
#if DEBUG
            Debug.Assert(!_isReadOnly, "Cannot write to a read-only ComponentStore");
#endif
            EnsureStoreCapacity();
            var count = _count;
            ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), count);
            entry.Entity = entity;
            entry.Kind = kind;
            entry.Value = value;
            _count = count + 1;
            if (kind != KindSet) _allSetKind = false;
        }

        public void AppendRemove(Entity entity)
        {
#if DEBUG
            Debug.Assert(!_isReadOnly, "Cannot write to a read-only ComponentStore");
#endif
            EnsureStoreCapacity();
            var count = _count;
            ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), count);
            entry.Entity = entity;
            entry.Kind = KindRemove;
            entry.Value = default;
            _count = count + 1;
            _allSetKind = false;
        }

        public void AppendConcurrent(Entity entity, in T value, byte kind)
        {
#if DEBUG
            Debug.Assert(!_isReadOnly, "Cannot write to a read-only ComponentStore");
#endif
            var local = GetLocal();
            if (local.Append(entity, value, kind))
                _hasLocalWrites = 1;
        }

        public override void SealParallelWrites()
        {
            if (_hasLocalWrites == 0)
                return;

            // Parallel writes may contain non-Set kinds; conservatively disable fast path.
            _allSetKind = false;

            var locals = _locals.Values;

            // Count total entries and find first non-empty local
            int total = _count, nonEmpty = 0;
            LocalBuffer? firstNonEmpty = null;
            foreach (var local in locals)
            {
                if (local.Count > 0)
                {
                    total += local.Count;
                    nonEmpty++;
                    firstNonEmpty ??= local;
                }
            }

            if (nonEmpty == 0)
            {
                _hasLocalWrites = 0;
                return;
            }

            // Steal: when _entries is empty and only one writer, steal its array.
            // This eliminates the Array.Copy entirely for the common single-writer case
            // and also for cases where serial Append happened on an empty store followed
            // by a single parallel writer.
            if (_count == 0 && nonEmpty == 1)
            {
                var oldEntries = _entries;
                _entries = firstNonEmpty!.Entries;
                _count = firstNonEmpty.Count;
                firstNonEmpty.Entries = oldEntries; // reuse old empty/small array
                firstNonEmpty.Count = 0;
                _hasLocalWrites = 0;
                return;
            }

            // Normal merge: copy all local buffers into _entries
            EnsureCapacity(total);

            var dst = _count;
            foreach (var local in locals)
            {
                var n = local.Count;
                if (n == 0) continue;

                Array.Copy(local.Entries, 0, _entries, dst, n);
                dst += n;
                local.Count = 0;
            }

            _count = dst;
            _hasLocalWrites = 0;
        }

        public override void Clear()
        {
            _count = 0;
            _allSetKind = true;
            if (_hasLocalWrites != 0)
            {
                foreach (var local in _locals.Values)
                    local.Count = 0;
                _hasLocalWrites = 0;
            }
        }

        public override void PruneStaleCommands(World world)
        {
            var write = 0;
            var allSetKind = true;

            for (var read = 0; read < _count; read++)
            {
                ref var entry = ref _entries[read];
                if (entry.Entity.IsPlaceholder || !world.IsAlive(entry.Entity))
                    continue;

                if (write != read)
                    _entries[write] = entry;

                if (_entries[write].Kind != KindSet)
                    allSetKind = false;

                write++;
            }

            _count = write;
            _allSetKind = allSetKind;
        }

        // ── Private helpers ──

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int required)
        {
            if ((uint)required <= (uint)_entries.Length) return;
            var newLen = _entries.Length == 0 ? 256 : _entries.Length;
            while (newLen < required) newLen *= 2;
            Array.Resize(ref _entries, newLen);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureStoreCapacity()
        {
            if (_count < _entries.Length) return;
            var newLen = _entries.Length == 0 ? 256 : _entries.Length * 2;
            Array.Resize(ref _entries, newLen);
        }

        // ── Read-only consumers (must be called AFTER SealParallelWrites) ──

        public override void ApplyToWorld(World world)
        {
            var count = _count;
            var compType = Component<T>.ComponentType;
            ref var entriesRef = ref MemoryMarshal.GetArrayDataReference(_entries);

            // Set-only fast path: every entry is KindSet, so we skip the per-entry
            // Kind branch and the lastArch invalidation that Add/Remove require.
            if (_allSetKind)
            {
                Archetype? fastArch = null;
                int fastColIdx = -1;
                int fastByteOffset = 0;
                bool fastIsChunked = false;

                if (world.IsChangeTrackingActive)
                {
                    for (var i = 0; i < count; i++)
                    {
                        ref var entry = ref Unsafe.Add(ref entriesRef, i);
                        var record = world.GetRecordFast(entry.Entity);
#if DEBUG
                        Debug.Assert(record.Archetype is not null && record.Version == entry.Entity.Version,
                            $"GetRecordFast returned stale or unoccupied record for entity {entry.Entity}.");
#endif
                        var arch = record.Archetype!;
                        if (arch != fastArch)
                        {
                            fastArch = arch;
                            if (!arch.TryGetComponentIndex(compType, out fastColIdx))
                                throw new InvalidOperationException(
                                    $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                            fastByteOffset = arch.GetColumnByteOffset(fastColIdx);
                            fastIsChunked = arch.IsChunked;
                        }

                        // Pre-hook: capture Old values before write
                        world.DispatchBeforeWrite(entry.Entity, arch, record.RowIndex);

                        // Previous-value capture (bucket path).
                        var typeId = compType.Value;
                        var buckets = world._previousBuckets;
                        var bucket = buckets is not null && (uint)typeId < (uint)buckets.Length
                            ? (Core.ValueChangeBucket<T>?)buckets[typeId]
                            : null;
                        if (bucket is not null)
                        {
                            var old = arch.GetComponentRefAt<T>(fastColIdx, record.RowIndex);
                            if (!fastIsChunked)
                                arch.SetComponentAtFlat<T>(fastColIdx, fastByteOffset, record.RowIndex, in entry.Value);
                            else
                                arch.SetComponentAtTyped(fastColIdx, record.RowIndex, in entry.Value);
                            bucket.Dispatch(entry.Entity, arch, in old, in entry.Value);
                        }
                        else
                        {
                            if (!fastIsChunked)
                                arch.SetComponentAtFlat<T>(fastColIdx, fastByteOffset, record.RowIndex, in entry.Value);
                            else
                                arch.SetComponentAtTyped(fastColIdx, record.RowIndex, in entry.Value);
                        }

                        // Post-hook: capture New values after write
                        world.DispatchAfterWrite(entry.Entity, arch, record.RowIndex);
                    }
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        ref var entry = ref Unsafe.Add(ref entriesRef, i);
                        var record = world.GetRecordFast(entry.Entity);
#if DEBUG
                        Debug.Assert(record.Archetype is not null && record.Version == entry.Entity.Version,
                            $"GetRecordFast returned stale or unoccupied record for entity {entry.Entity}.");
#endif
                        var arch = record.Archetype!;
                        if (arch != fastArch)
                        {
                            fastArch = arch;
                            if (!arch.TryGetComponentIndex(compType, out fastColIdx))
                                throw new InvalidOperationException(
                                    $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                            fastByteOffset = arch.GetColumnByteOffset(fastColIdx);
                            fastIsChunked = arch.IsChunked;
                        }
                        if (!fastIsChunked)
                            arch.SetComponentAtFlatNoTrack<T>(fastColIdx, fastByteOffset, record.RowIndex, in entry.Value);
                        else
                            arch.SetComponentAtTypedNoTrack(fastColIdx, record.RowIndex, in entry.Value);
                    }
                }
                return;
            }

            // Mixed-kind path: full Kind dispatch + archetype cache invalidation.
            Archetype? lastArchMixed = null;
            int lastColIdx = -1;
            int lastByteOffsetMixed = 0;
            bool lastIsChunkedMixed = false;

            for (var i = 0; i < count; i++)
            {
                ref var entry = ref Unsafe.Add(ref entriesRef, i);
                var record = world.GetRecordFast(entry.Entity);
#if DEBUG
                Debug.Assert(record.Archetype is not null && record.Version == entry.Entity.Version,
                    $"GetRecordFast returned stale or unoccupied record for entity {entry.Entity}.");
#endif

                if (entry.Kind == KindSet)
                {
                    var arch = record.Archetype!;
                    if (arch != lastArchMixed)
                    {
                        lastArchMixed = arch;
                        if (!arch.TryGetComponentIndex(compType, out lastColIdx))
                            throw new InvalidOperationException(
                                $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                        lastByteOffsetMixed = arch.GetColumnByteOffset(lastColIdx);
                        lastIsChunkedMixed = arch.IsChunked;
                    }

                    // Pre-hook: capture Old values before write
                    world.DispatchBeforeWrite(entry.Entity, arch, record.RowIndex);

                    // Previous-value capture (bucket path).
                    var typeId = compType.Value;
                    var buckets = world._previousBuckets;
                    var bucket = buckets is not null && (uint)typeId < (uint)buckets.Length
                        ? (Core.ValueChangeBucket<T>?)buckets[typeId]
                        : null;
                    if (bucket is not null)
                    {
                        var old = arch.GetComponentRefAt<T>(lastColIdx, record.RowIndex);
                        if (!lastIsChunkedMixed)
                            arch.SetComponentAtFlat<T>(lastColIdx, lastByteOffsetMixed, record.RowIndex, in entry.Value);
                        else
                            arch.SetComponentAtTyped(lastColIdx, record.RowIndex, in entry.Value);
                        bucket.Dispatch(entry.Entity, arch, in old, in entry.Value);
                    }
                    else
                    {
                        if (!lastIsChunkedMixed)
                            arch.SetComponentAtFlat<T>(lastColIdx, lastByteOffsetMixed, record.RowIndex, in entry.Value);
                        else
                            arch.SetComponentAtTyped(lastColIdx, record.RowIndex, in entry.Value);
                    }

                    // Post-hook: capture New values after write
                    world.DispatchAfterWrite(entry.Entity, arch, record.RowIndex);
                }
                else
                {
                    lastArchMixed = null;
                    if (entry.Kind == KindAdd)
                        world.ApplyTypedAdd(entry.Entity, record, compType, in entry.Value);
                    else
                        world.RemoveBoxed(entry.Entity, record, compType);
                }
            }
        }

        public override void EmitToDelta(FrameDelta delta)
        {
            var compType = Component<T>.ComponentType;
            var size = Unsafe.SizeOf<T>();
            for (var i = 0; i < _count; i++)
            {
                switch (_entries[i].Kind)
                {
                    case KindAdd:
                        unsafe
                        {
                            fixed (T* pFixed = &_entries[i].Value)
                                delta.AddAddUnsafe(_entries[i].Entity, compType, (byte*)pFixed, size);
                        }
                        break;
                    case KindSet:
                        unsafe
                        {
                            fixed (T* pFixed = &_entries[i].Value)
                                delta.AddSetUnsafe(_entries[i].Entity, compType, (byte*)pFixed, size);
                        }
                        break;
                    case KindRemove:
                        delta.AddRemove(_entries[i].Entity, compType);
                        break;
                }
            }
        }

        public override void ReplacePlaceholders(Entity[] resolveMap)
        {
            var typeId = Component<T>.ComponentType;
            var offsets = EntityFieldResolver.GetOffsets(typeId);
            var dataSpan = new ReadOnlySpan<Entity>(resolveMap);

            for (var i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[i];

                if (entry.Entity.IsPlaceholder)
                {
                    var resolved = resolveMap[entry.Entity.Version];
                    if (resolved.Id >= 0) entry.Entity = resolved;
                }

                if (offsets.Length > 0 && entry.Kind != KindRemove)
                {
                    EntityFieldResolver.ResolveInPlace(
                        MemoryMarshal.AsBytes(new Span<T>(ref entry.Value)),
                        typeId, dataSpan);
                }
            }
        }

        protected internal override void ForEachEntityEntry(Entity entity, ref ComponentMerger merger)
        {
            var ct = Component<T>.ComponentType;
            for (var i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.Entity == entity)
                {
                    merger.Add(ct, entry.Kind,
                        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref entry.Value)));
                }
            }
        }
    }

    internal readonly record struct HierarchyIntent(bool IsAdd, Entity Parent);

    private protected struct BatchedComponent
    {
        public ComponentType Type;
        public int Offset;
        public int Size;
        public bool Removed;
        public int Next;
    }

    private protected readonly struct PendingBatchView
    {
        public readonly bool[] Canceled;
        public readonly int[] Heads;
        public readonly int[] CompCounts;
        public readonly BatchedComponent[] Comps;
        public readonly byte[] Buf;
        public readonly Entity[] Entities;
        public readonly int Count;

        public PendingBatchView(bool[] canceled, int[] heads, int[] compCounts,
            BatchedComponent[] comps, byte[] buf, Entity[] entities, int count)
        {
            Canceled = canceled;
            Heads = heads;
            CompCounts = compCounts;
            Comps = comps;
            Buf = buf;
            Entities = entities;
            Count = count;
        }
    }

    private protected sealed class FrozenState
    {
        public ComponentStore?[] Stores;
        public Entity[] DestroyEntities;
        public int DestroyCount;
        public Dictionary<Entity, HierarchyIntent> HierarchyByChild;
        public int[] PendingBatch;
        public int PendingBatchCount;
        public int[] BatchHeads;
        public int[] BatchCompCounts;
        public BatchedComponent[] BatchComps;
        public byte[] BatchBuf;
        public Entity[] BatchEntities;
        public bool[] BatchCanceled;
        public int CancelledBatchCount;

#if DEBUG
        internal bool _isReadOnly;

        internal void AssertWritable()
        {
            if (_isReadOnly)
                throw new InvalidOperationException("Attempt to mutate a frozen FrozenState");
        }
#endif

        public FrozenState(int storeCount)
        {
            Stores = new ComponentStore?[storeCount];
            DestroyEntities = [];
            DestroyCount = 0;
            HierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
            PendingBatch = [];
            PendingBatchCount = 0;
            BatchHeads = [];
            BatchCompCounts = [];
            BatchComps = [];
            BatchBuf = [];
            BatchEntities = [];
            BatchCanceled = [];
            CancelledBatchCount = 0;
        }

        public PendingBatchView Pending => new(
            BatchCanceled, BatchHeads, BatchCompCounts, BatchComps,
            BatchBuf, BatchEntities, PendingBatchCount);
    }

}
