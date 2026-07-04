using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands with per-component-type append-only stores.
/// Add/Set/Remove on existing entities are stored inline in typed arrays —no entry stream,
/// no per-entity dedup. Created entities use a pending batch buffer for pre-materialization
/// component accumulation.
/// </summary>
public sealed class CommandStream
{
    private readonly World _world;
    // Pooled mutable buffer bundle. _frozen holds the current recording state;
    // _spareFrozen is a recycled standby (when the background worker is done);
    // _pendingFrozen holds state handed off to the background BuildFromFrozen worker.
    // A single reference swap in SwapOutState replaces the old 13-field-by-field
    // swap, eliminating a class of silent correctness bugs that only surface on
    // the async path.
    private FrozenState _frozen;
    private FrozenState? _spareFrozen;
    private FrozenState? _pendingFrozen;
    private Task? _pendingTask;

    // Scalars that live outside FrozenState (only needed during recording/reset,
    // not by the background worker).
    private int _pendingBatchMin = int.MaxValue;
    private int _pendingBatchMax;
    private int _batchCompTotal;
    private int _batchBufLen;

    // Local archetype cache keyed by ComponentMask.
    private const int MaskCacheSize = 8;
    private MaskCacheSlot[] _maskCache = [];
    private int _maskCacheCount;
    private int _maskCacheGeneration = -1;
    private ComponentMask _lastMask;
    private Archetype? _lastMaskArchetype;

    private readonly record struct MaskCacheSlot(ComponentMask Mask, Archetype Archetype);

    // Fast path for Add/Set immediately after Create on the same entity.
    private Entity _lastCreated;
    private int _lastCreatedBatch = -1;

    // ── EntitySlot tracking ──────────────────────────────────────────
    // Registration array indexed by placeholder seq. Each entry is a linked
    // list of Slot objects that want to be notified when this seq is resolved.
    // Cleared (Array.Clear + _trackedMaxSeq=0) after each resolution pass.
    private Slot?[] _trackedBySeq = [];
    private int _trackedMaxSeq;

    // ── Construction ───────────────────────────────────────────────────

    public CommandStream(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _frozen = new FrozenState(ComponentRegistry.Shared.ComponentTypeCount);
    }

    /// <summary>
    /// When enabled, all record methods (<see cref="Set{T}"/>, <see cref="Add{T}"/>,
    /// <see cref="Remove{T}"/>, <see cref="Create"/>, <see cref="Clone"/>,
    /// <see cref="Destroy"/>, <see cref="AddChild"/> and <see cref="RemoveChild"/>)
    /// can be called safely from multiple threads.
    /// <see cref="Submit"/> must be called from a single thread after all parallel work completes.
    /// Disable before returning to single-threaded use.
    /// Do not record concurrently into multiple <see cref="CommandStream"/> instances that target
    /// the same <see cref="World"/>; concurrent recording is only supported within one stream.
    /// </summary>
    public bool ParallelRecording
    {
        get => _parallelMode;
        set => _parallelMode = value;
    }

    private volatile bool _parallelMode;

    /// <summary>
    /// When <c>true</c>, <see cref="Create"/> and <see cref="Clone"/> produce
    /// placeholder entities (negative ids) instead of allocating real ids from
    /// the host <see cref="World"/>. <see cref="Snapshot"/> then emits a
    /// placeholder-id <see cref="FrameDelta"/> suitable for multi-host lockstep
    /// where each peer owns an independent <see cref="World"/> and id allocator.
    /// When <c>false</c> (default), <see cref="Create"/> and <see cref="Clone"/>
    /// allocate real ids immediately, and <see cref="Snapshot"/> resolves any
    /// deferred placeholders before building a real-id delta.
    ///
    /// When <c>DeferredEntities</c> is <c>true</c>, component fields of type
    /// <see cref="Entity"/> that reference deferred-created entities are
    /// automatically resolved by both <see cref="Submit"/> and
    /// <see cref="World.Replay(global::MiniArch.Core.FrameDelta)"/>.
    /// You can freely store a placeholder returned by <see cref="Create"/>
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

    private bool _deferredEntities;

    // ── Record API ────────────────────────────────────────────────────

    public Entity Create()
    {
        if (_parallelMode)
        {
            lock (_storeCreateLock)
                return _deferredEntities ? CreateDeferredImpl() : CreateImpl();
        }
        return _deferredEntities ? CreateDeferredImpl() : CreateImpl();
    }

    // ── EntitySlot API ───────────────────────────────────────────────

    /// <summary>
    /// Creates a tracked handle for <paramref name="entity"/> that auto-updates
    /// when a deferred placeholder is resolved during Submit or Replay.
    /// </summary>
    /// <param name="entity">A placeholder from <see cref="Create"/> (deferred mode)
    /// or any real entity (non-deferred mode).</param>
    /// <returns>
    /// An <see cref="EntitySlot"/> whose <see cref="EntitySlot.Value"/> returns
    /// the placeholder before resolution and the real entity after.
    /// </returns>
    /// <remarks>
    /// <para>
    /// In deferred mode, one small heap object is allocated per call (the internal
    /// <c>Slot</c>). In non-deferred mode (when <paramref name="entity"/> is already
    /// a real entity), no allocation occurs —the entity is stored inline.
    /// </para>
    /// <para>
    /// Track the entity in the same frame you create it. Call before Submit/Snapshot.
    /// </para>
    /// </remarks>
    public EntitySlot Track(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return new EntitySlot(entity);

        var slot = new Slot { Entity = entity };
        var seq = entity.Version;

        EnsureCapacity(ref _trackedBySeq, seq, 16);
        slot.Next = _trackedBySeq[seq];
        _trackedBySeq[seq] = slot;
        if (seq >= _trackedMaxSeq) _trackedMaxSeq = seq + 1;

        return new EntitySlot(slot);
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

    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        if (_parallelMode)
        {
            if (CanRecordParallelComponentCommand(entity))
                GetOrCreateStoreParallel<T>().AppendConcurrent(entity, component, KindAdd);
            return;
        }
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else if (_world.IsAlive(entity))
        {
            GetOrCreateStore<T>().Append(entity, component, KindAdd);
        }
    }

    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        if (_parallelMode)
        {
            if (CanRecordParallelComponentCommand(entity))
                GetOrCreateStoreParallel<T>().AppendConcurrent(entity, component, KindSet);
            return;
        }
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else if (_world.IsAlive(entity))
        {
            GetOrCreateStore<T>().Append(entity, component, KindSet);
        }
    }

    public void Remove<T>(Entity entity) where T : unmanaged
    {
        if (_parallelMode)
        {
            if (CanRecordParallelComponentCommand(entity))
                GetOrCreateStoreParallel<T>().AppendConcurrent(entity, default!, KindRemove);
            return;
        }
        if (_frozen.PendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        }
        else if (_world.IsAlive(entity))
        {
            GetOrCreateStore<T>().AppendRemove(entity);
        }
    }

    public void Destroy(Entity entity)
    {
        if (_parallelMode)
        {
            AppendDestroyConcurrent(entity);
            return;
        }

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

    public void AddChild(Entity parent, Entity child)
    {
        if (_parallelMode)
        {
            lock (_storeCreateLock)
                _frozen.HierarchyByChild[child] = new HierarchyIntent(true, parent);
            return;
        }
        _frozen.HierarchyByChild[child] = new HierarchyIntent(true, parent);
    }

    public void RemoveChild(Entity child)
    {
        if (_parallelMode)
        {
            lock (_storeCreateLock)
                _frozen.HierarchyByChild[child] = new HierarchyIntent(false, default);
            return;
        }
        _frozen.HierarchyByChild[child] = new HierarchyIntent(false, default);
    }

    public Entity Clone(Entity source)
    {
        if (!_world.TryGetLocation(source, out var location))
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

        return _parallelMode
            ? CloneConcurrent(source, location)
            : CloneImpl(source, location);
    }

    private Entity CloneConcurrent(Entity source, EntityInfo info)
    {
        lock (_storeCreateLock)
            return CloneImpl(source, info);
    }

    private Entity CloneImpl(Entity source, EntityInfo info)
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

    private void CloneComponents(Entity source, EntityInfo info, Entity clone, int batchIdx)
    {
        var archetype = info.Archetype;
        var sourceRow = info.RowIndex;
        var components = archetype.Signature.AsSpan();

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
            CommitBatchComponent(batchIdx, ct, offset, size);
        }

        CloneChildrenRecursive(source, clone);
    }

    // ── Submit ────────────────────────────────────────────────────────

    public bool Submit()
    {
        if (!HasAnyCommands())
            return false;

        var submitted = false;
        try
        {
            // Order matches BuildDelta: Create —Hierarchy —Ops —Destroy.
            // Keeping Submit and Snapshot aligned lets hosts use Submit on source and
            // Replay on replica without diverging for combined command patterns.
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

    private bool HasAnyCommands()
    {
        if (_frozen.PendingBatchCount > 0 || _frozen.DestroyCount > 0 || _frozen.HierarchyByChild.Count > 0)
            return true;
        for (var i = 0; i < _frozen.Stores.Length; i++)
            if (_frozen.Stores[i]?.HasCommands == true)
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
    /// call <c>Snapshot()</c> then <c>Clear()</c>. The source host then
    /// replays the delta back into its own world —together with all peer
    /// deltas —achieving the deterministic multi-host guarantee.
    /// </para>
    /// </remarks>
    public FrameDelta Snapshot()
    {
        if (!_deferredEntities)
        {
            ResolveDeferredCreates();
            var delta = new FrameDelta();
            BuildDelta(delta);
            return delta;
        }
        ThrowIfSnapshotHasImmediateEntities();
        var d = new FrameDelta();
        BuildDelta(d);
        return d;
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
        if (!HasAnyCommands())
            return Task.FromResult(new FrameDelta());

        var frozen = SwapOutState();
        // Static delegate + state parameter avoids the per-call closure allocation
        // that Task.Run(() => ...) would create. FrozenState is a reference type,
        // so passing it as `object` is a free upcast —no boxing.
        var task = Task.Factory.StartNew(
            s_buildFromFrozen, frozen, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        SubmitFromFrozen(frozen);
        _pendingFrozen = frozen;
        _pendingTask = task;
        return task;
    }

    private static readonly Func<object?, FrameDelta> s_buildFromFrozen =
        state => BuildFromFrozen((FrozenState)state!);

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
        _frozen.HierarchyByChild.Clear();
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
        ComponentType[]? pooledTypes = null;
        int[]? pooledOffsets = null;
        Span<ComponentType> typesFromBatch = rawCount <= 64
            ? stackalloc ComponentType[rawCount]
            : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(rawCount)).AsSpan(0, rawCount);
        Span<int> offsets = rawCount <= 64
            ? stackalloc int[rawCount]
            : (pooledOffsets = ArrayPool<int>.Shared.Rent(rawCount)).AsSpan(0, rawCount);
        try
        {
            ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, b7 = 0;
            var idx = 0;
            var hasLargeIds = false;
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
                            typesFromBatch[idx] = comp.Type;
                            offsets[idx] = comp.Offset;
                            idx++;
                        }
                    }
                    else
                    {
                        hasLargeIds = true;
                        var seen = false;
                        for (var j = 0; j < idx; j++)
                        {
                            if (typesFromBatch[j].Value == id) { seen = true; break; }
                        }
                        if (!seen)
                        {
                            typesFromBatch[idx] = comp.Type;
                            offsets[idx] = comp.Offset;
                            idx++;
                        }
                    }
                }
                current = comp.Next;
            }

            if (idx == 0)
            {
                _world.MaterializeEmptyReservedEntity(entity);
                return;
            }

            Archetype archetype;
            if (hasLargeIds)
            {
                if (idx > 1)
                {
                    SortTypesAndOffsets(typesFromBatch[..idx], offsets[..idx]);
                    idx = DeduplicateSortedSpans(typesFromBatch[..idx], offsets[..idx]);
                }
                var typeArray = typesFromBatch[..idx].ToArray();
                archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(typeArray));
            }
            else
            {
                var mask = new ComponentMask(b0, b1, b2, b3, b4, b5, b6, b7);
                archetype = ResolveArchetypeForMask(mask);
            }

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
            if (pooledTypes != null) ArrayPool<ComponentType>.Shared.Return(pooledTypes);
            if (pooledOffsets != null) ArrayPool<int>.Shared.Return(pooledOffsets);
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
        var batchEntities = view.Entities;

        var entity = batchEntities[i];
        var rawCount = batchCompCounts[i];
        if (rawCount == 0)
        {
            delta.AddCreate(entity, Array.Empty<RawComponentValue>());
            return;
        }

        var comps = new RawComponentValue[rawCount];
        var outIdx = 0;
        var current = batchHeads[i];
        while (current >= 0)
        {
            ref var bc = ref batchComps[current];
            if (!bc.Removed)
                comps[outIdx++] = ReadRawFromBuf(batchBuf, bc);
            current = bc.Next;
        }

        if (outIdx == 0)
        {
            delta.AddCreate(entity, Array.Empty<RawComponentValue>());
            return;
        }

        if (outIdx != comps.Length)
            Array.Resize(ref comps, outIdx);
        if (outIdx > 1)
            outIdx = SortAndDeduplicateComponents(comps);
        if (outIdx != comps.Length)
            Array.Resize(ref comps, outIdx);
        delta.AddCreate(entity, comps);
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

        var sorted = new KeyValuePair<Entity, HierarchyIntent>[hierarchyByChild.Count];
        ((ICollection<KeyValuePair<Entity, HierarchyIntent>>)hierarchyByChild).CopyTo(sorted, 0);
        Array.Sort(sorted, (a, b) => a.Key.Id.CompareTo(b.Key.Id));

        foreach (var (child, intent) in sorted)
        {
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

    private static void ApplyHierarchyToWorld(World world, FrozenState frozen)
    {
        if (frozen.HierarchyByChild.Count == 0) return;

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
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
    private bool TryGetPendingBatch(Entity entity, out int batchIdx)
    {
        if (entity == _lastCreated && _lastCreatedBatch >= 0)
        {
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
                    return true;
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
            {
                var grown = ArrayPool<Entity>.Shared.Rent(queue.Length * 2);
                Array.Copy(queue, grown, queueCount);
                ArrayPool<Entity>.Shared.Return(queue);
                queue = grown;
            }
            queue[queueCount++] = child;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePendingComponent<T>(int batchIdx, T component) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var offset = ReserveBatchBufSpace(size);
        Unsafe.WriteUnaligned(ref _frozen.BatchBuf[offset], component);
        CommitBatchComponent(batchIdx, CommandTypeInfo<T>.Type, offset, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReserveBatchBufSpace(int size)
    {
        if (_batchBufLen + size > _frozen.BatchBuf.Length)
            Array.Resize(ref _frozen.BatchBuf, Math.Max(_batchBufLen + size, _frozen.BatchBuf.Length == 0 ? 4096 : _frozen.BatchBuf.Length * 2));
        var offset = _batchBufLen;
        _batchBufLen += size;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitBatchComponent(int batchIdx, ComponentType type, int offset, int size)
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

    private void MarkBatchComponentRemoved(int batchIdx, ComponentType targetType)
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

    private Archetype ResolveArchetypeForMask(ComponentMask mask)
    {
        if (mask.IsZero())
            return _world.GetOrCreateArchetype(Signature.Empty);

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

        var types = MaskToTypes(mask);
        var archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(types));

        if (_maskCache.Length == 0)
            _maskCache = new MaskCacheSlot[MaskCacheSize];

        var slotIdx = _maskCacheCount < MaskCacheSize
            ? _maskCacheCount++
            : (int)(mask.B0 ^ mask.B1 ^ mask.B2 ^ mask.B3 ^ mask.B4 ^ mask.B5 ^ mask.B6 ^ mask.B7) & (MaskCacheSize - 1);

        _maskCache[slotIdx] = new MaskCacheSlot(mask, archetype);
        _lastMask = mask;
        _lastMaskArchetype = archetype;
        return archetype;
    }

    private static ComponentType[] MaskToTypes(ComponentMask mask)
    {
        var count = BitOperations.PopCount(mask.B0) + BitOperations.PopCount(mask.B1)
                  + BitOperations.PopCount(mask.B2) + BitOperations.PopCount(mask.B3)
                  + BitOperations.PopCount(mask.B4) + BitOperations.PopCount(mask.B5)
                  + BitOperations.PopCount(mask.B6) + BitOperations.PopCount(mask.B7);
        var types = new ComponentType[count];
        var idx = 0;
        CollectBits(mask.B0, 0, types, ref idx);
        CollectBits(mask.B1, 64, types, ref idx);
        CollectBits(mask.B2, 128, types, ref idx);
        CollectBits(mask.B3, 192, types, ref idx);
        CollectBits(mask.B4, 256, types, ref idx);
        CollectBits(mask.B5, 320, types, ref idx);
        CollectBits(mask.B6, 384, types, ref idx);
        CollectBits(mask.B7, 448, types, ref idx);
        return types;
    }

    private static void CollectBits(ulong bits, int baseValue, ComponentType[] types, ref int idx)
    {
        while (bits != 0)
        {
            var tz = BitOperations.TrailingZeroCount(bits);
            types[idx++] = new ComponentType(baseValue + tz);
            bits &= bits - 1;
        }
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

    private void CloneChildrenRecursive(Entity sourceRoot, Entity cloneRoot)
    {
        if (!_world.Hierarchy.HasChildren(sourceRoot)) return;

        var stack = ArrayPool<Entity>.Shared.Rent(32);
        var cloneStack = ArrayPool<Entity>.Shared.Rent(32);
        var stackCount = 0;

        try
        {
            foreach (var child in _world.Hierarchy.EnumerateChildren(_world, sourceRoot))
            {
                if (stackCount >= stack.Length)
                {
                    var newStack = ArrayPool<Entity>.Shared.Rent(stack.Length * 2);
                    var newCloneStack = ArrayPool<Entity>.Shared.Rent(stack.Length * 2);
                    Array.Copy(stack, newStack, stackCount);
                    Array.Copy(cloneStack, newCloneStack, stackCount);
                    ArrayPool<Entity>.Shared.Return(stack);
                    ArrayPool<Entity>.Shared.Return(cloneStack);
                    stack = newStack;
                    cloneStack = newCloneStack;
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
                if (!_world.TryGetLocation(srcChild, out var childLocation)) continue;

                var cloneChild = Create();
                int batchIdx;
                if (_deferredEntities)
                    TryGetPendingBatch(cloneChild, out batchIdx);
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
                AddChild(cloneParent, cloneChild);

                foreach (var grandChild in _world.Hierarchy.EnumerateChildren(_world, srcChild))
                {
                    if (stackCount >= stack.Length)
                    {
                        var newStack = ArrayPool<Entity>.Shared.Rent(stack.Length * 2);
                        var newCloneStack = ArrayPool<Entity>.Shared.Rent(stack.Length * 2);
                        Array.Copy(stack, newStack, stackCount);
                        Array.Copy(cloneStack, newCloneStack, stackCount);
                        ArrayPool<Entity>.Shared.Return(stack);
                        ArrayPool<Entity>.Shared.Return(cloneStack);
                        stack = newStack;
                        cloneStack = newCloneStack;
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

    // ── Sorting / dedup ───────────────────────────────────────────────

    private static void SortTypesAndOffsets(Span<ComponentType> types, Span<int> offsets)
    {
        for (var i = 1; i < types.Length; i++)
        {
            var keyType = types[i];
            var keyOffset = offsets[i];
            var j = i - 1;
            while (j >= 0 && types[j].Value > keyType.Value)
            {
                types[j + 1] = types[j];
                offsets[j + 1] = offsets[j];
                j--;
            }
            types[j + 1] = keyType;
            offsets[j + 1] = keyOffset;
        }
    }

    private static int DeduplicateSortedSpans(Span<ComponentType> types, Span<int> offsets)
    {
        var writeIdx = 0;
        for (var readIdx = 0; readIdx < types.Length; readIdx++)
        {
            if (writeIdx == 0 || types[readIdx] != types[writeIdx - 1])
            {
                if (writeIdx != readIdx)
                {
                    types[writeIdx] = types[readIdx];
                    offsets[writeIdx] = offsets[readIdx];
                }
                writeIdx++;
            }
        }
        return writeIdx;
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

    private bool CanRecordParallelComponentCommand(Entity entity)
    {
        if (_world.IsAlive(entity))
            return true;

        lock (_storeCreateLock)
        {
            return TryGetPendingBatch(entity, out _);
        }
    }

    private void AppendDestroyConcurrent(Entity entity)
    {
        var slot = Interlocked.Increment(ref _frozen.DestroyCount) - 1;
        lock (_storeCreateLock)
        {
            while (slot >= _frozen.DestroyEntities.Length)
            {
                var newLen = _frozen.DestroyEntities.Length == 0 ? 64 : _frozen.DestroyEntities.Length * 2;
                while (newLen <= slot) newLen *= 2;
                Array.Resize(ref _frozen.DestroyEntities, newLen);
            }
            _frozen.DestroyEntities[slot] = entity;
        }
    }

    // ── Store management ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentStore<T> GetOrCreateStore<T>() where T : unmanaged
    {
        var id = CommandTypeInfo<T>.Type.Value;
        Debug.Assert(id >= 0, "Component type id must be non-negative.");

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
        if (store == null)
        {
            store = new ComponentStore<T>();
            slot = store;
        }

        Debug.Assert(store is ComponentStore<T>,
            $"Slot {id} holds {store.GetType()} but expected ComponentStore<{typeof(T)}>.");
        return Unsafe.As<ComponentStore<T>>(store);
    }

    private ComponentStore<T> GetOrCreateStoreParallel<T>() where T : unmanaged
    {
        var id = CommandTypeInfo<T>.Type.Value;
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
        if (store == null)
        {
            lock (_storeCreateLock)
            {
                store = _frozen.Stores[id];
                if (store == null)
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

    private readonly object _storeCreateLock = new();

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
            if (_frozen.BatchCanceled[i]) continue;
            var entity = _frozen.BatchEntities[i];
            if (entity.Id < 0) continue;
            _frozen.PendingBatch[entity.Id] = -1;
            if (releaseReserved) _world.TryReleaseReserved(entity);
        }
        _deferredSeq = 0;
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
    /// Called after <see cref="Replay(FrameDelta)"/> when the delta is this
    /// stream's own (detected via <see cref="FrameDelta.OriginStream"/>).
    /// </summary>
    private void ResolveTrackedSlotsFromReplay()
    {
        if (_trackedMaxSeq == 0) return;

        var max = Math.Min(_trackedMaxSeq, _trackedBySeq.Length);
        for (var seq = 0; seq < max; seq++)
        {
            var s = _trackedBySeq[seq];
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

            _trackedBySeq[seq] = null;
        }

        _trackedMaxSeq = 0;
    }

    private void ResolveDeferredCreates()
    {
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

    private static class CommandTypeInfo<T> where T : unmanaged
    {
        public static readonly ComponentType Type = Component<T>.ComponentType;
    }

    // ── Internal types ────────────────────────────────────────────────

    private const byte KindAdd = 0;
    private const byte KindSet = 1;
    private const byte KindRemove = 2;

    internal abstract class ComponentStore
    {
        public abstract bool HasCommands { get; }
        public abstract void ApplyToWorld(World world);
        public abstract void EmitToDelta(FrameDelta delta);
        public abstract void Clear();
        public abstract void ReplacePlaceholders(Entity[] resolveMap);
    }

    private sealed class ComponentStore<T> : ComponentStore where T : unmanaged
    {
        private T[] _data = [];
        private Entity[] _entities = [];
        private byte[] _kinds = [];
        private int _count;
        private readonly object _resizeLock = new();

        public override bool HasCommands => _count > 0;

        public void Append(Entity entity, in T value, byte kind)
        {
            EnsureStoreCapacity();
            var count = _count;
            ref var entitiesRef = ref MemoryMarshal.GetArrayDataReference(_entities);
            ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_data);
            ref var kindsRef = ref MemoryMarshal.GetArrayDataReference(_kinds);
            Unsafe.Add(ref entitiesRef, count) = entity;
            Unsafe.Add(ref dataRef, count) = value;
            Unsafe.Add(ref kindsRef, count) = kind;
            _count = count + 1;
        }

        public void AppendRemove(Entity entity)
        {
            EnsureStoreCapacity();
            var count = _count;
            ref var entitiesRef = ref MemoryMarshal.GetArrayDataReference(_entities);
            ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_data);
            ref var kindsRef = ref MemoryMarshal.GetArrayDataReference(_kinds);
            Unsafe.Add(ref entitiesRef, count) = entity;
            Unsafe.Add(ref dataRef, count) = default;
            Unsafe.Add(ref kindsRef, count) = KindRemove;
            _count = count + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AppendConcurrent(Entity entity, in T value, byte kind)
        {
            var slot = Interlocked.Increment(ref _count) - 1;
            while (slot >= _data.Length)
            {
                lock (_resizeLock)
                {
                    if (slot < _data.Length) break;
                    var newLen = _data.Length == 0 ? 256 : _data.Length;
                    while (newLen <= slot) newLen *= 2;
                    Array.Resize(ref _data, newLen);
                    Array.Resize(ref _entities, newLen);
                    Array.Resize(ref _kinds, newLen);
                }
            }
            _entities[slot] = entity;
            _data[slot] = value;
            _kinds[slot] = kind;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureStoreCapacity()
        {
            if (_count < _data.Length) return;
            var newLen = _data.Length == 0 ? 256 : _data.Length * 2;
            Array.Resize(ref _data, newLen);
            Array.Resize(ref _entities, newLen);
            Array.Resize(ref _kinds, newLen);
        }

        public override void ApplyToWorld(World world)
        {
            // Cache for consecutive Set operations on the same archetype:
            // resolves the component column index once per archetype instead
            // of once per entity.
            Archetype? lastArch = null;
            int lastColIdx = -1;
            var count = _count;

            ref var entitiesRef = ref MemoryMarshal.GetArrayDataReference(_entities);
            ref var kindsRef = ref MemoryMarshal.GetArrayDataReference(_kinds);
            ref var dataRef = ref MemoryMarshal.GetArrayDataReference(_data);

            for (var i = 0; i < count; i++)
            {
                var entity = Unsafe.Add(ref entitiesRef, i);
                if (!world.TryGetRecord(entity, out var record)) continue;

                var kind = Unsafe.Add(ref kindsRef, i);
                if (kind == KindSet)
                {
                    var arch = record.Archetype!;
                    int colIdx;
                    if (arch == lastArch)
                    {
                        colIdx = lastColIdx;
                    }
                    else
                    {
                        lastArch = arch;
                        colIdx = lastColIdx = arch.GetComponentIndex(Component<T>.ComponentType);
                    }
                    arch.SetComponentAtTyped(colIdx, record.RowIndex, in Unsafe.Add(ref dataRef, i));
                }
                else
                {
                    lastArch = null; // structural change invalidates cache
                    if (kind == KindAdd)
                        world.ApplyTypedAdd(entity, record, Component<T>.ComponentType, in Unsafe.Add(ref dataRef, i));
                    else
                        world.RemoveBoxed(entity, record, Component<T>.ComponentType);
                }
            }
        }

        public override void EmitToDelta(FrameDelta delta)
        {
            var compType = Component<T>.ComponentType;
            var size = Unsafe.SizeOf<T>();
            for (var i = 0; i < _count; i++)
            {
                switch (_kinds[i])
                {
                    case KindAdd:
                        unsafe
                        {
                            // AddAddUnsafe may grow the delta buffer and trigger a compacting GC;
                            // keep the source element pinned for the whole raw write.
                            fixed (T* pFixed = &_data[i])
                                delta.AddAddUnsafe(_entities[i], compType, (byte*)pFixed, size);
                        }
                        break;
                    case KindSet:
                        unsafe
                        {
                            // AddSetUnsafe may grow the delta buffer and trigger a compacting GC;
                            // keep the source element pinned for the whole raw write.
                            fixed (T* pFixed = &_data[i])
                                delta.AddSetUnsafe(_entities[i], compType, (byte*)pFixed, size);
                        }
                        break;
                    case KindRemove:
                        delta.AddRemove(_entities[i], compType);
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
                // Resolve the command-target entity (existing behaviour).
                ref var slot = ref _entities[i];
                if (slot.IsPlaceholder)
                {
                    var resolved = resolveMap[slot.Version];
                    if (resolved.Id >= 0) slot = resolved;
                }

                // Resolve embedded Entity refs in the component value itself.
                // Remove operations carry no meaningful payload —skip them.
                if (offsets.Length > 0 && _kinds[i] != KindRemove)
                {
                    EntityFieldResolver.ResolveInPlace(
                        MemoryMarshal.AsBytes(new Span<T>(ref _data[i])),
                        typeId, dataSpan);
                }
            }
        }

        public override void Clear() => _count = 0;
    }

    private readonly record struct HierarchyIntent(bool IsAdd, Entity Parent);

    private struct BatchedComponent
    {
        public ComponentType Type;
        public int Offset;
        public int Size;
        public bool Removed;
        public int Next;
    }

    private readonly struct PendingBatchView
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

    private sealed class FrozenState
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

    // ── EntitySlot types ──────────────────────────────────────────────

    /// <summary>
    /// Internal mutable state shared between all copies of an <see cref="EntitySlot"/>.
    /// One instance is allocated per <see cref="CommandStream.Track"/> call on a placeholder entity.
    /// </summary>
    internal sealed class Slot
    {
        /// <summary>The current entity value: placeholder before resolution, real after.</summary>
        internal Entity Entity;

        /// <summary>Linked-list pointer for registration in <c>_trackedBySeq</c>. Nulled after resolution.</summary>
        internal Slot? Next;
    }

    /// <summary>
    /// A tracked entity handle that auto-updates when a deferred placeholder is resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Obtain an EntitySlot via <see cref="CommandStream.Track"/>. The <see cref="Value"/>
    /// property returns the placeholder entity before resolution and the real entity after
    /// <see cref="CommandStream.Submit"/> or <see cref="CommandStream.Replay(FrameDelta)"/>.
    /// </para>
    /// <para>
    /// <b>EntitySlot cannot be stored in ECS components</b> (it contains reference types and
    /// is not <c>unmanaged</c>). Store <see cref="Entity"/> (via <c>slot.Value</c>) in
    /// component fields instead —the existing <c>EntityFieldResolver</c> handles auto-resolution
    /// of component fields independently.
    /// </para>
    /// </remarks>
    public readonly struct EntitySlot
    {
        private readonly Entity _entity;
        private readonly Slot? _slot;

        /// <summary>Creates an EntitySlot wrapping an inline real entity (non-deferred mode).</summary>
        internal EntitySlot(Entity entity)
        {
            _entity = entity;
            _slot = null;
        }

        /// <summary>Creates an EntitySlot wrapping a mutable Slot (deferred mode).</summary>
        internal EntitySlot(Slot slot)
        {
            _entity = default;
            _slot = slot;
        }

        /// <summary>
        /// The current entity. Returns the placeholder before resolution,
        /// the real entity after Submit/Replay.
        /// </summary>
        public Entity Value => _slot is not null ? _slot.Entity : _entity;

        /// <summary>Whether this slot holds a non-default entity handle.</summary>
        public bool HasValue => Value != default;
    }
}