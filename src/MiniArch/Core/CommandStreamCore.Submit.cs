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
    /// Validates that every command recorded this frame is legal. Executes the
    /// pure-validation stages of the submit preflight sequence in their original
    /// order: batch dedup scan + embedded placeholder lifecycle, component store
    /// presence validation, and hierarchy overlay cycle check. Idempotent: running
    /// <see cref="Validate"/> before <see cref="Submit"/> does not change the result
    /// <see cref="Submit"/> produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Side effects are limited to idempotent internal preparation
    /// (<see cref="PrepareStores"/>: seal parallel writes, prune stale store
    /// commands, and build set-location cache scratch). No command is consumed
    /// and no <see cref="World"/> state is mutated.
    /// </para>
    /// <para>
    /// Any invalid state throws <see cref="InvalidOperationException"/> at the
    /// first violation, with the violating entity id and/or component type in the
    /// message.
    /// </para>
    /// <para>
    /// <b>Not covered.</b> CreateMany group consistency (a group modified with
    /// Set/Add/Remove after the CreateMany call, or a duplicate/mask-invalid
    /// writer), slot-reservation state, and the FrameDelta budget
    /// (<see cref="FrameDelta.MaxFrameBytes"/>/<see cref="FrameDelta.MaxOpsPerFrame"/>)
    /// are checked by the consume path at their original timing, not by
    /// <see cref="Validate"/>.
    /// </para>
    /// </remarks>
    public void Validate()
    {
        PrepareStores(buildSetLocationCache: true);
        if (!HasAnyCommands())
            return;

        // Order matches the pure-validation stages of Submit's preflight sequence:
        // embedded placeholder lifecycle (with last-wins batch dedup), component
        // store presence, then hierarchy overlay.
        PreflightEmbeddedPlaceholders(useFlagFastPath: false);
        PreflightComponentStores(_frozen);
        PreflightHierarchyOverlay(_world, _frozen);
    }

    /// <summary>
    /// Applies all recorded commands to the world and returns true if any work was performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Zero validation by default —with one opt-out.</b> <see cref="Submit"/>
    /// no longer runs the contract preflights (component store presence, hierarchy
    /// overlay). Recorded-command errors surface at their apply-time consumption
    /// point instead of before any world mutation, and earlier commands may
    /// already be applied before the violation is detected (partial application;
    /// reserved ids are released by the internal cleanup). Call
    /// <see cref="Validate"/> before <see cref="Submit"/> to detect all recorded
    /// contract violations up front, at the first violation, without world mutation.
    /// </para>
    /// <para>
    /// One class of errors is still rejected atomically before any mutation:
    /// component values referencing a cancelled or unknown deferred placeholder,
    /// and unresolvable component layouts (nested <see cref="Entity"/> fields /
    /// LayoutKind.Auto with Entity fields). These are rejected in the consume
    /// path —before id reservation, free-list realignment and materialization —
    /// with the same contract and message text as <see cref="Validate"/>, so a
    /// violating frame can never be applied locally or emitted into a
    /// <see cref="FrameDelta"/> that a replaying host would reject.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A recorded command violates its consume-time contract: a component value
    /// references a cancelled or unknown deferred placeholder (rejected before
    /// any mutation), an Add targets an existing component or a Set targets a
    /// missing component, or a hierarchy entity is stale/unknown (these surface
    /// at apply time, after earlier commands may have been applied). Use
    /// <see cref="Validate"/> to fail before any mutation.
    /// </exception>
    public bool Submit()
    {
        PrepareStores(buildSetLocationCache: true);
        if (!HasAnyCommands())
            return false;

        var submitted = false;
        try
        {
            // Order matches BuildDelta: Create —Hierarchy —Ops —Destroy.
            // Keeping Submit and Snapshot aligned lets hosts use Submit on source and
            // Replay on replica without diverging for combined command patterns.
            //
            // Validation (embedded placeholder lifecycle, component store presence,
            // hierarchy overlay) is opt-in via Validate() — the default path runs
            // zero preflight checks. What remains is the functional pipeline: the
            // slot-reservation invariant check (defense-in-depth), free-list
            // alignment, deferred resolution, then materialize/apply/destroy.
            //
            // Before any free-list mutations, align the cancelled-batch entries to
            // match the wire emission order. CancelPendingEntity pushes free-list
            // entries in user destroy-order during recording, but Replay processes
            // Release ops in batch (creation) order. The batch-order realignment
            // below corrects this divergence so the source's free-list matches the
            // shadow's after Replay.
            //
            // Embedded-placeholder / layout rejection (T2.7) is flag-driven: the
            // record path probes every written value (per-type static verdict) and
            // sets a frame flag only when a value may reference a placeholder (or
            // its layout is unresolvable). The scan below runs the full last-wins
            // pass only for flagged frames — before id reservation, free-list
            // realignment and materialization (atomic; failures consume no id).
            PreValidatePendingSlots();
            PreflightEmbeddedPlaceholders();
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
        var useSetLocationCache = CanReuseSetLocationCache(frozen);

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
                _world, _preflightGenerations, _preflightPresence, _preflightEpoch, useSetLocationCache);
        }
    }

    private void ApplyDestroys()
    {
        for (var i = 0; i < _frozen.DestroyCount; i++)
        {
            var entity = _frozen.DestroyEntities[i];
            // The entity may already have been cascade-destroyed if a parent
            // earlier in the DestroyEntities array was destroyed.
            _world.DestroyIfAlive(entity);
        }
    }

    private void PreflightEmbeddedPlaceholders(bool useFlagFastPath = true)
    {
        // T2.7: the record path flags this frame only when a written value may
        // contain a placeholder ref (or its layout could not be verified). A frame
        // with no such value cannot fail this preflight, so skip the full scan.
        // Validate() passes useFlagFastPath: false — an explicit full scan.
        if (useFlagFastPath &&
            Volatile.Read(ref _frozen.MayNeedEmbeddedPlaceholderPreflight) == 0)
            return;

        RejectBatchEmbeddedPlaceholders();

        foreach (var store in _frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.PreflightEmbeddedPlaceholders(this);
        }
    }

    /// <summary>
    /// T2.7 record-path probe: sets the frame flag when a written component value
    /// may contain a placeholder ref (or its layout cannot be verified), so the
    /// submit/snapshot scan skips its full pass for placeholder-free frames. Never
    /// throws; layout-verification failures surface at the scan's original timing,
    /// before any world/allocator mutation. Per-type verdict via
    /// <see cref="FieldKinds{T}"/> —a static field read (no array lookup) —so the
    /// placeholder-free hot path costs one hoistable read + one perfectly predicted
    /// branch. The flag is monotonic within a frame, so probing stops after the
    /// first hit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void FlagFrameIfMayContainPlaceholder<T>(in T data) where T : unmanaged
    {
        // T2.8: single readonly static field read per written value. Types without
        // Entity fields (the common case) pay one load + one perfectly predicted
        // branch. Types with Entity fields probe each field for a placeholder and
        // set the frame flag (monotonic, so probing stops after the first hit).
        // Unresolvable layouts set HasEntityFields conservatively, so the submit-
        // time scan runs and throws the layout error at its original timing.
        if (!FieldKinds<T>.HasEntityFields)
            return;

        var offsets = FieldKinds<T>.Offsets;
        if (offsets.Length == 0)
        {
            Interlocked.Exchange(ref _frozen.MayNeedEmbeddedPlaceholderPreflight, 1);
            return;
        }

        var bytes = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1));
        for (var i = 0; i < offsets.Length; i++)
        {
            if (MemoryMarshal.Read<Entity>(bytes[offsets[i]..]).IsPlaceholder)
            {
                Interlocked.Exchange(ref _frozen.MayNeedEmbeddedPlaceholderPreflight, 1);
                return;
            }
        }
    }

    /// <summary>
    /// Non-generic record-path probe for raw/dynamic component values imported
    /// into the batch buffer (Clone's archetype raw copy — see
    /// <see cref="CommandStreamCore.CloneMaterializedComponents"/> and
    /// <see cref="ParallelCommandStream.Clone"/>). Same contract as the generic
    /// probe, but takes a <see cref="ComponentType"/> + raw bytes because Clone
    /// copies bytes, not typed values. <see cref="EntityFieldResolver.GetOffsets"/>
    /// success flags the frame only when an actual top-level Entity field holds a
    /// placeholder; an unresolvable layout (nested Entity / LayoutKind.Auto with
    /// Entity fields) is caught and flagged conservatively — probing never
    /// throws, the consume-time scan surfaces the layout error at its original
    /// timing (before any world/allocator mutation). Monotonic frame flag:
    /// probing stops after the first hit.
    /// </summary>
    private protected void FlagFrameIfMayContainPlaceholder(ComponentType type, ReadOnlySpan<byte> data)
    {
        // Monotonic flag: once set, later probes have nothing to add.
        if (Volatile.Read(ref _frozen.MayNeedEmbeddedPlaceholderPreflight) == 1)
            return;

        ReadOnlySpan<int> offsets;
        try
        {
            offsets = EntityFieldResolver.GetOffsets(type);
        }
        catch (InvalidOperationException)
        {
            // Unresolvable layout: conservative —force the frame scan, which
            // throws the layout error at its original timing (before any
            // world/allocator mutation), mirroring FieldKinds<T>.
            Interlocked.Exchange(ref _frozen.MayNeedEmbeddedPlaceholderPreflight, 1);
            return;
        }

        if (offsets.IsEmpty)
            return;

        for (var i = 0; i < offsets.Length; i++)
        {
            if (MemoryMarshal.Read<Entity>(data[offsets[i]..]).IsPlaceholder)
            {
                Interlocked.Exchange(ref _frozen.MayNeedEmbeddedPlaceholderPreflight, 1);
                return;
            }
        }
    }

    // ── Consume-side embedded-placeholder / layout rejection (T2.5) ────
    //
    // Runs in the consume path (Submit / Snapshot / SnapshotInto / async handoff)
    // before any id reservation, free-list realignment, materialization, delta
    // emission or worker handoff. Rejects: (a) component values referencing a
    // cancelled or unknown deferred placeholder, and (b) component types whose
    // layout cannot be resolved (nested Entity / LayoutKind.Auto with Entity
    // fields) — the same contract and message text as Validate(). Because it runs
    // before ResolveDeferredCreates, a rejection consumes no allocator id/version
    // (the pre-T2.5 preflight boundary) and prevents source/replica divergence.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureScanFieldKind(int id)
    {
        var arr = _scanFieldKind;
        if ((uint)id < (uint)arr.Length)
            return;
        var newLen = arr.Length == 0 ? 64 : arr.Length;
        while (newLen <= id) newLen *= 2;
        Array.Resize(ref _scanFieldKind, newLen);
    }

    /// <summary>
    /// True when <paramref name="embedded"/> is a placeholder created by this
    /// frame's deferred Create (same stream, not cancelled) —still legal at
    /// consume-entry time; it is replaced with a real id by
    /// <see cref="ResolveDeferredCreates"/> before materialization/emission.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsValidDeferredPlaceholder(Entity embedded)
    {
        var seq = embedded.Version;
        if ((uint)seq >= (uint)_deferredSeq)
            return false;
        var batchIdx = _pendingBatchDeferredArr[seq];
        return (uint)batchIdx < (uint)_frozen.PendingBatchCount &&
            !_frozen.BatchCanceled[batchIdx];
    }

    /// <summary>
    /// Rejects a component value whose effective <see cref="Entity"/> fields
    /// reference a cancelled or unknown deferred placeholder. Shared message
    /// text with the store-level check, so Validate() and the consume path fail
    /// identically. <paramref name="owner"/> is the entity the value belongs to.
    /// </summary>
    private void RejectEmbeddedPlaceholders(
        ReadOnlySpan<byte> data, ComponentType componentType, ReadOnlySpan<int> offsets, Entity owner)
    {
        foreach (var offset in offsets)
        {
            var embedded = MemoryMarshal.Read<Entity>(data[offset..]);
            if (!embedded.IsPlaceholder)
                continue;
            if (IsValidDeferredPlaceholder(embedded))
                continue;

            throw new InvalidOperationException(
                $"Component type id {componentType.Value} on entity {owner} references " +
                $"cancelled or unknown placeholder {embedded}.");
        }
    }

    /// <summary>
    /// Scans every non-cancelled pending batch's effective last-wins component
    /// values, rejecting cancelled/unknown embedded placeholder refs and
    /// unresolvable layouts. Only Entity-field-bearing component types are
    /// value-scanned (per-type verdict cached in <see cref="_scanFieldKind"/>);
    /// the batch chain is walked head-first so only the newest non-removed value
    /// of each type is examined —matching materialize/emit last-wins semantics
    /// without a second dedup pass. Shared by <see cref="PreflightEmbeddedPlaceholders"/>
    /// (Validate) and the consume entry points (Submit/Snapshot/async handoff).
    /// </summary>
    /// <remarks>
    /// Performance: the hot loop is fully inlined (no helper calls per component)
    /// — one bounds-checked byte read for the per-type verdict, then a value scan
    /// only for Entity-field-bearing types. The verdict array is monotonic, so the
    /// steady-state cost is one L1 read + one branch per batch component.
    /// </remarks>
    private void RejectBatchEmbeddedPlaceholders()
    {
        var pending = _frozen.Pending;
        var comps = pending.Comps;
        var buf = pending.Buf;
        var batchCanceled = pending.Canceled;
        var batchEntities = pending.Entities;
        var kinds = _scanFieldKind;

        for (var batchIdx = 0; batchIdx < pending.Count; batchIdx++)
        {
            if (batchCanceled[batchIdx])
                continue;

            var entity = batchEntities[batchIdx];
            var current = pending.Heads[batchIdx];

            // Last-wins: the chain head is the newest write; the first
            // non-removed value of each Entity-field-bearing type is the
            // effective one —older duplicates are superseded.
            ulong s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, s5 = 0, s6 = 0, s7 = 0;
            while (current >= 0)
            {
                ref var comp = ref comps[current];
                if (!comp.Removed)
                {
                    var id = comp.Type.Value;
                    var kind = (uint)id < (uint)kinds.Length ? kinds[id] : (byte)0;
                    if (kind == 0)
                    {
                        kind = JudgeAndCacheFieldKind(id, ref kinds);
                        kinds = _scanFieldKind;
                    }

                    if (kind == 2)
                    {
                        if (id < 512)
                        {
                            if (IsSeen(ref s0, ref s1, ref s2, ref s3,
                                       ref s4, ref s5, ref s6, ref s7, id))
                            {
                                current = comp.Next;
                                continue;
                            }
                        }
                        else if (HasNewerEffectiveComponentType(
                                     comps, pending.Heads[batchIdx], current, id))
                        {
                            current = comp.Next;
                            continue;
                        }

                        // Inline value scan: only Entity-field-bearing values reach
                        // here; real references (the hot case) cost one read + one
                        // branch before the continue.
                        var offsets = EntityFieldResolver.GetOffsets(comp.Type);
                        var data = buf.AsSpan(comp.Offset, comp.Size);
                        foreach (var off in offsets)
                        {
                            var embedded = MemoryMarshal.Read<Entity>(data[off..]);
                            if (!embedded.IsPlaceholder)
                                continue;
                            if (IsValidDeferredPlaceholder(embedded))
                                continue;
                            throw new InvalidOperationException(
                                $"Component type id {comp.Type.Value} on entity {entity} references " +
                                $"cancelled or unknown placeholder {embedded}.");
                        }
                    }
                }
                current = comp.Next;
            }
        }
    }

    private byte JudgeAndCacheFieldKind(int id, ref byte[] kinds)
    {
        // GetOffsets resolves and caches the layout; throws for nested Entity /
        // LayoutKind.Auto with Entity fields (same contract as Validate()).
        var kind = EntityFieldResolver.GetOffsets(new ComponentType(id)).IsEmpty ? (byte)1 : (byte)2;
        EnsureScanFieldKind(id);
        _scanFieldKind[id] = kind;
        kinds = _scanFieldKind;
        return kind;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeen(ref ulong s0, ref ulong s1, ref ulong s2, ref ulong s3,
        ref ulong s4, ref ulong s5, ref ulong s6, ref ulong s7, int id)
    {
        if (id < 64)      { var bit = 1UL << id;        if ((s0 & bit) != 0) return true; s0 |= bit; return false; }
        if (id < 128)     { var bit = 1UL << (id - 64);  if ((s1 & bit) != 0) return true; s1 |= bit; return false; }
        if (id < 192)     { var bit = 1UL << (id - 128); if ((s2 & bit) != 0) return true; s2 |= bit; return false; }
        if (id < 256)     { var bit = 1UL << (id - 192); if ((s3 & bit) != 0) return true; s3 |= bit; return false; }
        if (id < 320)     { var bit = 1UL << (id - 256); if ((s4 & bit) != 0) return true; s4 |= bit; return false; }
        if (id < 384)     { var bit = 1UL << (id - 320); if ((s5 & bit) != 0) return true; s5 |= bit; return false; }
        if (id < 448)     { var bit = 1UL << (id - 384); if ((s6 & bit) != 0) return true; s6 |= bit; return false; }
        var b7 = 1UL << (id - 448);                      if ((s7 & b7) != 0) return true; s7 |= b7; return false;
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
    /// <para>
    /// <b>Zero validation by default —with one opt-out.</b> <see cref="Snapshot"/>
    /// no longer runs the contract preflights (component store presence, hierarchy
    /// overlay). A violating frame may therefore emit a delta whose replay fails on
    /// a receiving host. Call <see cref="Validate"/> before <see cref="Snapshot"/>
    /// to reject all recorded contract violations up front.
    /// </para>
    /// <para>
    /// One class of errors is still rejected atomically before emission: component
    /// values referencing a cancelled or unknown deferred placeholder, and
    /// unresolvable component layouts (nested <see cref="Entity"/> fields /
    /// LayoutKind.Auto with Entity fields). These are rejected in the consume path
    /// —before any deferred resolution, target clear or delta emission — so a
    /// violating frame never produces a delta a replaying host would reject.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The recorded frame exceeds <see cref="FrameDelta.MaxFrameBytes"/> or
    /// <see cref="FrameDelta.MaxOpsPerFrame"/>, or a deferred placeholder cannot be
    /// resolved in non-deferred mode.
    /// </exception>
    public FrameDelta Snapshot()
    {
        PrepareStores();
        if (_deferredEntities)
            ThrowIfSnapshotHasImmediateEntities();

        // Embedded-placeholder / layout rejection (T2.7) is flag-driven: the
        // record path sets the frame flag only for values that may reference a
        // placeholder; the scan below runs only for flagged frames — before any
        // deferred resolution or delta emission (no divergence).
        PreflightEmbeddedPlaceholders();
        PreflightFrameDeltaBudget(_deferredEntities);
        if (!_deferredEntities)
            ResolveDeferredCreates();

        var delta = new FrameDelta();
        delta.EnsureCapacity(GetSnapshotCapacityHint());
        BuildDelta(delta);
        _pendingReplay = true;
        return delta;
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
    /// <para>
    /// <b>Zero validation by default —with one opt-out.</b> <see cref="SnapshotInto"/>
    /// no longer runs the contract preflights (component store presence, hierarchy
    /// overlay). Call <see cref="Validate"/> before <see cref="SnapshotInto"/> to
    /// reject all recorded contract violations up front.
    /// </para>
    /// <para>
    /// One class of errors is still rejected atomically before emission: component
    /// values referencing a cancelled or unknown deferred placeholder, and
    /// unresolvable component layouts — so a violating frame never produces a delta
    /// a replaying host would reject, and <paramref name="target"/> is left
    /// untouched.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The recorded frame exceeds <see cref="FrameDelta.MaxFrameBytes"/> or
    /// <see cref="FrameDelta.MaxOpsPerFrame"/>, or a deferred placeholder cannot be
    /// resolved in non-deferred mode.
    /// </exception>
    public void SnapshotInto(FrameDelta target)
    {
        ArgumentNullException.ThrowIfNull(target);
        PrepareStores();
        if (_deferredEntities)
            ThrowIfSnapshotHasImmediateEntities();

        // Embedded-placeholder / layout rejection (T2.7) is flag-driven: the
        // scan below runs only for flagged frames — before any deferred
        // resolution, target clear or delta emission.
        PreflightEmbeddedPlaceholders();
        PreflightFrameDeltaBudget(_deferredEntities);
        if (!_deferredEntities)
            ResolveDeferredCreates();

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
    /// <exception cref="InvalidOperationException">
    /// The recorded frame exceeds a FrameDelta budget (detected before the World is
    /// submitted), or a consume-time contract violation surfaces during submit.
    /// Embedded-placeholder/layout violations are rejected before the submit/worker
    /// handoff (atomically, no mutations); other consume-time violations may leave
    /// earlier commands applied. Call <see cref="Validate"/> before this method to
    /// fail before any mutation.
    /// </exception>
    public Task<FrameDelta> SubmitAndSnapshotAsync()
    {
        PrepareStores(buildSetLocationCache: true);
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
    /// <para>
    /// <b>On failure, <paramref name="target"/> content is undefined.</b>
    /// Embedded-placeholder/layout violations are rejected before the worker
    /// starts and leave <paramref name="target"/> untouched; other consume-time
    /// violations may clear or partially write <paramref name="target"/> (the
    /// worker may have begun building the delta before the failure surfaces).
    /// Treat <paramref name="target"/> as unusable after an exception.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A <see cref="Task"/> that completes when the background delta-building
    /// work is done. The caller should await this task before reading
    /// <paramref name="target"/>. The submit (world apply) runs synchronously
    /// on the calling thread before the returned task.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The recorded frame exceeds a FrameDelta budget, or a consume-time contract
    /// violation surfaces during submit. Embedded-placeholder/layout violations are
    /// rejected before the submit/worker handoff (atomically, no mutations); other
    /// consume-time violations may leave earlier commands applied. Call
    /// <see cref="Validate"/> before this method to fail before any mutation.
    /// </exception>
    public Task SubmitAndSnapshotIntoAsync(FrameDelta target)
    {
        ArgumentNullException.ThrowIfNull(target);
        PrepareStores(buildSetLocationCache: true);
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
            // validation is opt-in (Validate()) — the default async path runs zero
            // preflight contract checks. What must still happen on the calling
            // thread before the worker owns the state: the slot-reservation
            // invariant check (defense-in-depth), the flag-driven embedded-
            // placeholder / layout scan (T2.7, only for flagged frames — before
            // any id reservation), the FrameDelta budget check (so an oversized
            // frame fails before free-list realignment, real-id reservation, state
            // swap and worker start), free-list alignment, and deferred resolution.
            PreValidatePendingSlots();
            PreflightEmbeddedPlaceholders();

            // Reject an oversized frame before free-list realignment or real-id
            // reservation. Unresolved placeholder endpoints are conservatively
            // priced at the maximum real Entity wire width.
            PreflightFrameDeltaBudget(deferredMode: false);

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

    private void PreflightFrameDeltaBudget(bool deferredMode)
    {
        if (IsFrameDeltaWithinUpperBound(deferredMode))
            return;

        var budget = new FrameDelta.Budget();
        AccumulatePendingEntitiesDeltaBudget(ref budget, _frozen.Pending, deferredMode);
        AccumulateHierarchyDeltaBudget(ref budget, _frozen, deferredMode);

        foreach (var store in _frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.AccumulateDeltaBudget(ref budget);
        }

        for (var i = 0; i < _frozen.DestroyCount; i++)
            budget.AddDestroy(_frozen.DestroyEntities[i]);
    }

    private bool IsFrameDeltaWithinUpperBound(bool deferredMode)
    {
        var activePendingCount = _frozen.PendingBatchCount - _frozen.CancelledBatchCount;
        var emittedPendingCount = activePendingCount;
        if (!deferredMode)
        {
            // Immediate cancelled batches emitted a real Reserve+Release and must
            // stay in the bound. Cancelled deferred batches never touched the
            // source allocator and remain placeholders after resolution, so the
            // real-id writer omits them entirely.
            emittedPendingCount = _frozen.PendingBatchCount;
            if (_frozen.CancelledBatchCount != 0)
            {
                for (var i = 0; i < _frozen.PendingBatchCount; i++)
                {
                    if (_frozen.BatchCanceled[i] && _frozen.BatchEntities[i].IsPlaceholder)
                        emittedPendingCount--;
                }
            }
        }

        var pendingOpCount = 2L * emittedPendingCount;
        var opCount = pendingOpCount + 2L * _frozen.HierarchyByChild.Count + _frozen.DestroyCount;

        // Maximum encoded sizes: entity-only op = 11 bytes; Create = 16 bytes
        // before component entries; AddChild = 21 bytes. Removed/duplicate batch
        // components remain in the bound, which makes it conservative.
        var byteCount = 27L * emittedPendingCount +
            _batchBufLen + 10L * _batchCompTotal +
            32L * _frozen.HierarchyByChild.Count + 11L * _frozen.DestroyCount;

        foreach (var store in _frozen.Stores)
        {
            if (store?.HasCommands != true)
                continue;

            opCount += store.DeltaOpCount;
            byteCount += store.DeltaByteUpperBound;
            if (opCount > FrameDelta.MaxOpsPerFrame || byteCount > FrameDelta.MaxFrameBytes)
                return false;
        }

        return opCount <= FrameDelta.MaxOpsPerFrame && byteCount <= FrameDelta.MaxFrameBytes;
    }

    private int GetSnapshotCapacityHint()
    {
        long estimate = 0;
        foreach (var store in _frozen.Stores)
        {
            if (store?.HasCommands != true)
                continue;

            estimate += store.ComponentDeltaCapacityHint;
            if (estimate >= FrameDelta.MaxFrameBytes)
                return FrameDelta.MaxFrameBytes;
        }

        return (int)estimate;
    }

    private void PrepareComponentStoresForConsume(bool buildSetLocationCache)
    {
        var hasStoreCommands = false;
        foreach (var store in _frozen.Stores)
        {
            if (store is null)
                continue;

            hasStoreCommands |= store.PrepareForConsume(_world, buildSetLocationCache);
        }
        _hasStoreCommands = hasStoreCommands;
    }

    /// <summary>
    /// Seals parallel stores then prunes stale component commands.
    /// Must be called before any Submit/Snapshot/SnapshotInto/SubmitAndSnapshotAsync/
    /// SubmitAndSnapshotIntoAsync operation. Not needed before Replay (no recording
    /// state to prepare).
    /// </summary>
    private void PrepareStores(bool buildSetLocationCache = false)
    {
        SealParallelStores();
        buildSetLocationCache &= CanReuseSetLocationCache(_frozen);
        PrepareComponentStoresForConsume(buildSetLocationCache);
    }

    private static bool CanReuseSetLocationCache(FrozenState frozen)
    {
        // A Set-only component store never migrates rows. If any store contains
        // Add/Remove, applying that earlier store can move entities before a
        // later store consumes its cached rows, so row reuse is disabled globally.
        foreach (var store in frozen.Stores)
        {
            if (store?.HasStructuralCommands == true)
                return false;
        }

        return true;
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

        _frozen.MayNeedEmbeddedPlaceholderPreflight = 0;
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
            _world.DestroyIfAlive(entity);
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
