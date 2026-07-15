using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public abstract partial class CommandStreamCore
{
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

    // ── CreateMany bulk materialization ──────────────────────────────

    /// <summary>
    /// Shared pending-batch materialization loop used by both
    /// <see cref="Submit"/> (via <see cref="MaterializeAllPending"/>) and
    /// <see cref="SubmitAndSnapshotAsync"/> (via <see cref="SubmitFromFrozen"/>).
    /// Walks batches in order; when a <see cref="CreateManyGroup"/> starts at the
    /// current batch index, it runs the bulk fast path
    /// (<see cref="MaterializeCreateManyGroup"/>). Any entity in the group
    /// that was modified with Set/Add/Remove after CreateMany is a programming
    /// error —the method throws <see cref="InvalidOperationException"/>.
    /// Non-group batches use the per-entity <see cref="MaterializePending"/>.
    /// </summary>
    private void MaterializePendingBatches(FrozenState frozen)
    {
        var view = new PendingBatchView(
            frozen.BatchCanceled, frozen.BatchHeads, frozen.BatchCompCounts,
            frozen.BatchComps, frozen.BatchBuf, frozen.BatchEntities, frozen.PendingBatchCount);
        var groupIdx = 0;
        var batchIdx = 0;
        while (batchIdx < frozen.PendingBatchCount)
        {
            var groupStart = -1;
            var groupCount = 0;
            if (groupIdx < frozen.CreateManyGroupCount)
            {
                groupStart = frozen.CreateManyGroups[groupIdx].StartBatch;
                groupCount = frozen.CreateManyGroups[groupIdx].Count;
            }

            if (groupStart == batchIdx)
            {
                MaterializeCreateManyGroup(frozen, view, groupIdx);
                batchIdx += groupCount;
                groupIdx++;
                continue;
            }

            if (!view.Canceled[batchIdx])
                MaterializePending(view, view.Entities[batchIdx], batchIdx);
            batchIdx++;
        }
    }

    [DoesNotReturn]
    private static void ThrowCreateManyMismatch(int groupIdx, int batchIdx)
    {
        throw new InvalidOperationException(
            $"CreateMany group #{groupIdx} at batch {batchIdx} was modified " +
            $"with Set/Add/Remove on one or more entities. " +
            $"CreateMany requires all entities in the group to have the exact " +
            $"same component set. Use per-entity Create() + Add() for heterogeneous initialization.");
    }

    [DoesNotReturn]
    private static void ThrowBufferOverflow()
    {
        throw new InvalidOperationException(
            "CommandStream batch buffer overflow: total pending component data " +
            "exceeds int.MaxValue. Reduce the number or size of batched components " +
            "per frame.");
    }

    [DoesNotReturn]
    private static void ThrowCreateManyMaskFailure(int groupIdx)
    {
        throw new InvalidOperationException(
            $"CreateMany group #{groupIdx} has invalid component type set: " +
            $"duplicate component types or a component type id >= 512. " +
            $"The writer struct must not emit duplicate component types.");
    }

    /// <summary>
    /// CreateMany bulk materialization fast path for one group.
    /// Preallocates all live rows via <see cref="Archetype.AllocateRows"/>,
    /// resolves the archetype once, then writes each entity's components in a
    /// tight loop. Throws <see cref="InvalidOperationException"/> if any entity
    /// in the group was modified with Set/Add/Remove after the CreateMany call,
    /// or if the writer contains duplicate component types.
    /// </summary>
    private unsafe void MaterializeCreateManyGroup(
        FrozenState frozen, in PendingBatchView view, int groupIdx)
    {
        ref readonly var group = ref frozen.CreateManyGroups[groupIdx];
        var startBatch = group.StartBatch;
        var count = group.Count;
        var componentCount = group.ComponentCount;

        // Allocate scratch arrays. Use stackalloc for small counts (≤64) and
        // ArrayPool for larger ones to avoid StackOverflowException.
        const int MaxStackCount = 64;
        ComponentType[]? pooledTypes = null;
        int[]? pooledIndices = null;
        Span<ComponentType> groupTypes = componentCount <= MaxStackCount
            ? stackalloc ComponentType[componentCount]
            : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(componentCount)).AsSpan(0, componentCount);
        Span<int> columnIndices = componentCount <= MaxStackCount
            ? stackalloc int[componentCount]
            : (pooledIndices = ArrayPool<int>.Shared.Rent(componentCount)).AsSpan(0, componentCount);
        try
        {
            FillGroupTypes(in group, groupTypes);

            // Verify preconditions on every non-cancelled batch in the group and
            // count live entities. Any mismatch is a programming error —fast-fail.
            var liveCount = 0;
            for (var i = 0; i < count; i++)
            {
                var batchIdx = startBatch + i;
                if (view.Canceled[batchIdx]) continue;
                if (view.CompCounts[batchIdx] != componentCount)
                    ThrowCreateManyMismatch(groupIdx, batchIdx);
                if (!ChainMatchesGroup(in view, batchIdx, groupTypes))
                    ThrowCreateManyMismatch(groupIdx, batchIdx);
                liveCount++;
            }

            if (liveCount == 0)
                return; // all cancelled —nothing to materialize

            // Build mask once. If any id >= 512 or any component type is duplicated,
            // the mask popcount will not equal componentCount. These are writer bugs
            // (e.g. duplicate component types) —fast-fail.
            var builder = new MaskBuilder();
            for (var i = 0; i < componentCount; i++)
                builder.SetBit(groupTypes[i].Value);
            var mask = builder.ToMask();
            if (!World.IsMaskCanonical(mask, componentCount))
                ThrowCreateManyMaskFailure(groupIdx);

            var archetype = ResolveArchetype(mask, groupTypes);

            // Precompute column indices once for all entities in the group.
            for (var i = 0; i < componentCount; i++)
                columnIndices[i] = archetype.GetComponentIndexFast(groupTypes[i]);

            // Allocate all live rows in one shot.
            var startRow = archetype.AllocateRows(liveCount);

            fixed (byte* bufPtr = view.Buf)
            {
                var row = startRow;
                for (var i = 0; i < count; i++)
                {
                    var batchIdx = startBatch + i;
                    if (view.Canceled[batchIdx]) continue;

                    var entity = view.Entities[batchIdx];
                    _world.MaterializeReservedEntityAt(entity, archetype, row);

                    // Chain is in reverse write order (LIFO insertion):
                    //   head -> CN -> CN-1 -> ... -> C1 -> -1
                    // So columnIndices[componentCount-1] matches the first chain
                    // entry, columnIndices[componentCount-2] the next, etc.
                    var current = view.Heads[batchIdx];
                    var typeIdx = componentCount - 1;
                    while (current >= 0)
                    {
                        ref var bc = ref view.Comps[current];
                        archetype.WriteComponentRaw(columnIndices[typeIdx], row, bufPtr + bc.Offset);
                        typeIdx--;
                        current = bc.Next;
                    }

                    row++;
                }
            }
        }
        finally
        {
            if (pooledTypes is not null)
                ArrayPool<ComponentType>.Shared.Return(pooledTypes);
            if (pooledIndices is not null)
                ArrayPool<int>.Shared.Return(pooledIndices);
        }
    }

    /// <summary>
    /// Copies the group's component types into the stack span (write order).
    /// </summary>
    private static void FillGroupTypes(in CreateManyGroup group, Span<ComponentType> types)
    {
        if (types.Length > 0) types[0] = group.C1;
        if (types.Length > 1) types[1] = group.C2;
        if (types.Length > 2) types[2] = group.C3;
        if (types.Length > 3) types[3] = group.C4;
        if (types.Length > 4) types[4] = group.C5;
        if (types.Length > 5) types[5] = group.C6;
        if (types.Length > 6) types[6] = group.C7;
        if (types.Length > 7) types[7] = group.C8;
    }

    /// <summary>
    /// Verifies that the batch's linked-list chain exactly matches the group's
    /// component types in reverse write order with no <see cref="BatchedComponent.Removed"/>
    /// entries. The chain was built by successive <see cref="CommitBatchComponent"/>
    /// calls (LIFO head insertion), so walking from head yields
    /// <c>groupTypes[N-1], groupTypes[N-2], ..., groupTypes[0]</c>.
    /// </summary>
    private static bool ChainMatchesGroup(in PendingBatchView view, int batchIdx,
        ReadOnlySpan<ComponentType> groupTypes)
    {
        var componentCount = groupTypes.Length;
        var current = view.Heads[batchIdx];
        for (var i = componentCount - 1; i >= 0; i--)
        {
            if (current < 0) return false;
            ref var bc = ref view.Comps[current];
            if (bc.Removed) return false;
            if (bc.Type != groupTypes[i]) return false;
            current = bc.Next;
        }
        return current < 0;
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


    // ── Pending entity helpers ────────────────────────────────────────

    private void GrowPendingBatchFor(int entityId)
    {
        if (entityId < _frozen.PendingBatch.Length) return;

        // Max reasonable size: .NET array limit is ~2.14B elements; cap growth
        // below that to prevent overflow-induced infinite loop.
        const int maxPendingBatch = 0x40000000; // ~1B entries
        var newLen = _frozen.PendingBatch.Length == 0 ? 64 : _frozen.PendingBatch.Length;
        while (newLen <= entityId)
        {
            if (newLen > maxPendingBatch / 2) { newLen = maxPendingBatch; break; }
            newLen *= 2;
        }
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
                    _submitEpoch = _world.ReservedReleaseEpoch;
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
        if (_batchBufLen > int.MaxValue - size)
            ThrowBufferOverflow();
        if (_batchBufLen + size > _frozen.BatchBuf.Length)
            Array.Resize(ref _frozen.BatchBuf, Math.Max(_batchBufLen + size,
                Math.Min(_frozen.BatchBuf.Length == 0 ? 4096 : _frozen.BatchBuf.Length * 2, 0x7FFFFFC7)));
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

    // ── CreateMany group tracking ────────────────────────────────────

    /// <summary>
    /// Appends one <see cref="CreateManyGroup"/> describing the contiguous batch
    /// range just produced by a CreateMany call. The fast-path submit consumer
    /// (<see cref="MaterializeCreateManyGroup"/>) uses this to bulk-allocate
    /// rows and write components in a tight loop. Must only be called after the
    /// per-entity loop fully completed —if the writer throws midway, no group is
    /// appended and the partial pending entities fall back to per-entity
    /// materialization via <see cref="MaterializePending"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendCreateManyGroup(int startBatch, int count, int componentCount,
        ComponentType c1, ComponentType c2, ComponentType c3, ComponentType c4,
        ComponentType c5, ComponentType c6, ComponentType c7, ComponentType c8)
    {
        EnsureCapacity(ref _frozen.CreateManyGroups, _frozen.CreateManyGroupCount, 16);
        _frozen.CreateManyGroups[_frozen.CreateManyGroupCount++] = new CreateManyGroup
        {
            StartBatch = startBatch,
            Count = count,
            ComponentCount = componentCount,
            C1 = c1, C2 = c2, C3 = c3, C4 = c4,
            C5 = c5, C6 = c6, C7 = c7, C8 = c8,
        };
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
    internal static bool TrySetBit(ref ulong b0, ref ulong b1, ref ulong b2, ref ulong b3,
                                  ref ulong b4, ref ulong b5, ref ulong b6, ref ulong b7, int id)
    {
        if (id < 64)      return TrySetBitInLane(ref b0, id);
        if (id < 128)     return TrySetBitInLane(ref b1, id - 64);
        if (id < 192)     return TrySetBitInLane(ref b2, id - 128);
        if (id < 256)     return TrySetBitInLane(ref b3, id - 192);
        if (id < 320)     return TrySetBitInLane(ref b4, id - 256);
        if (id < 384)     return TrySetBitInLane(ref b5, id - 320);
        if (id < 448)     return TrySetBitInLane(ref b6, id - 384);
        if (id < 512)     return TrySetBitInLane(ref b7, id - 448);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySetBitInLane(ref ulong lane, int bitIndex)
    {
        var bit = 1UL << bitIndex;
        if ((lane & bit) != 0) return false;
        lane |= bit;
        return true;
    }

}
