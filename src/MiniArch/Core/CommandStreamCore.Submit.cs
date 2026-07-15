using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public abstract partial class CommandStreamCore
{
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
            PreValidatePendingSlots();
            PreflightComponentStores(_frozen);
            PreflightHierarchyOverlay(_world, _frozen);
            AlignCancelledBatchFreeListOrder();
            ResolveDeferredCreates();
            PreValidatePendingSlots();
            _submitEpoch = _world.ReservedReleaseEpoch;
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

    private bool HasAnyCommands()
    {
        if (_frozen.PendingBatchCount > 0 || _frozen.DestroyCount > 0 || _frozen.HierarchyByChild.Count > 0)
            return true;
        if (_hasStoreCommands)
            return true;
        return false;
    }

    /// <summary>
    /// Pre-validates that all non-cancelled pending batches still have their slots
    /// in reserved state before any materialization occurs. Throws
    /// <see cref="InvalidOperationException"/> if a slot is no longer reserved,
    /// preventing partial materialization without rollback.
    /// </summary>
    /// <remarks>
    /// This is defense-in-depth. In normal use, reserved slots stay reserved until
    /// materialized. The check guards against external corruption or internal
    /// inconsistency (e.g. a slot released by mistake between recording and submit).
    /// Cancelled batches are intentionally skipped —their reservations may have
    /// been released by design.
    /// </remarks>
    private void PreValidatePendingSlots() => PreValidatePendingSlots(_frozen);

    private void PreValidatePendingSlots(FrozenState frozen)
    {
        // Fast path: no release has happened since our last epoch sync,
        // so all pending slots are still guaranteed to be reserved.
        if (_world.ReservedReleaseEpoch == _submitEpoch)
            return;

        for (var i = 0; i < frozen.PendingBatchCount; i++)
        {
            if (frozen.BatchCanceled[i]) continue;
            var entity = frozen.BatchEntities[i];
            if (entity.Id < 0) continue; // deferred placeholder —not yet resolved
            if (!_world.IsSlotReserved(entity))
            {
                throw new InvalidOperationException(
                    $"Pending entity {entity} (batch {i}) is no longer in reserved state. " +
                    "Cannot materialize a slot that is not reserved.");
            }
        }
    }

    private void MaterializeAllPending()
        => MaterializePendingBatches(_frozen);

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

    private void PreflightComponentStores(FrozenState frozen)
    {
        var cacheSetRows = true;
        foreach (var store in frozen.Stores)
        {
            if (store?.HasStructuralCommands == true)
            {
                cacheSetRows = false;
                break;
            }
        }

        var required = _world.EntitySlotCount;
        if (_preflightGenerations.Length < required)
        {
            var newLength = Math.Max(required, Math.Max(16, _preflightGenerations.Length * 2));
            Array.Resize(ref _preflightGenerations, newLength);
            Array.Resize(ref _preflightPresence, newLength);
        }

        foreach (var store in frozen.Stores)
        {
            if (store?.HasCommands != true)
                continue;

            if (_preflightEpoch == int.MaxValue)
            {
                Array.Clear(_preflightGenerations);
                _preflightEpoch = 1;
            }
            else
            {
                _preflightEpoch++;
            }

            store.PreflightValidate(
                _world, _preflightGenerations, _preflightPresence, _preflightEpoch, cacheSetRows);
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
        ArgumentNullException.ThrowIfNull(delta);
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

        PrepareAsyncHandoff();
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

        _pendingFrozen = frozen;
        _pendingTask = task;
        SubmitFrozenWhileWorkerOwnsState(frozen, task);
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
        ArgumentNullException.ThrowIfNull(target);
        PrepareStores();
        if (!HasAnyCommands())
        {
            target.Clear();
            return Task.CompletedTask;
        }

        PrepareAsyncHandoff();
        var frozen = SwapOutState();
#if DEBUG
        _world._deferredEpoch++;
        _pendingBatchDeferredEpoch = [];
#endif
        var task = Task.Factory.StartNew(
            s_buildFromFrozenInto, (frozen, target), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        _pendingFrozen = frozen;
        _pendingTask = task;
        SubmitFrozenWhileWorkerOwnsState(frozen, task);
        return task;
    }

    private void PrepareAsyncHandoff()
    {
        try
        {
            // Contract failures must be discovered while the commands are still
            // owned by the calling thread. Starting the worker first would let it
            // observe a frame that the local World rejects and would leave the
            // detached FrozenState without tracked ownership when Submit throws.
            PreValidatePendingSlots();
            PreflightComponentStores(_frozen);
            PreflightHierarchyOverlay(_world, _frozen);

            // Keep allocator ordering identical to Submit/Replay before deferred
            // placeholders are resolved into authoritative real ids.
            AlignCancelledBatchFreeListOrder();
            ResolveDeferredCreates();
            PreValidatePendingSlots();
        }
        catch
        {
            Clear(releaseReserved: true);
            throw;
        }
    }

    private void SubmitFrozenWhileWorkerOwnsState(FrozenState frozen, Task worker)
    {
        try
        {
            SubmitFromFrozen(frozen);
        }
        catch
        {
            // The worker may still be reading frozen. Observe its completion before
            // allowing the state to be recycled, while preserving the Submit error.
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch
            {
                // Submit is the authoritative failure for this synchronous call.
            }

            TryReclaimPending();
            throw;
        }
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
        foreach (var store in _spareFrozen.Stores)
            if (store is not null) store._isReadOnly = false;
#endif
    }

    private FrozenState SwapOutState()
    {
        TryReclaimPending();

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
        _frozen.CreateManyGroupCount = 0;
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
        foreach (var store in frozen.Stores)
            if (store is not null) store._isReadOnly = true;
#endif

        return frozen;
    }

    private void SubmitFromFrozen(FrozenState frozen)
    {
        // Order matches Submit and BuildDelta: Create —Hierarchy —Ops —Destroy.
        PreValidatePendingSlots(frozen);
        _submitEpoch = _world.ReservedReleaseEpoch;
        MaterializePendingBatches(frozen);

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

}
