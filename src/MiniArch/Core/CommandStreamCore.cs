using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
public abstract partial class CommandStreamCore
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

    // Epoch guard: tracks _world.ReservedReleaseEpoch at the last known
    // consistent state. PreValidatePendingSlots fast-path compares this against
    // _world.ReservedReleaseEpoch to skip the O(N) scan when no release has occurred.
    private int _submitEpoch;

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

    // Reused by component preflight. Each typed store gets a fresh epoch, so
    // presence state is isolated per component type without clearing the arrays.
    private int[] _preflightGenerations = [];
    private byte[] _preflightPresence = [];
    private int _preflightEpoch;

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
        _submitEpoch = _world.ReservedReleaseEpoch;
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
        var rawCount = _frozen.BatchCompCounts[srcBatchIdx];

        int[]? pooledIndices = null;
        Span<int> indices = rawCount <= 64
            ? stackalloc int[rawCount]
            : (pooledIndices = ArrayPool<int>.Shared.Rent(rawCount)).AsSpan(0, rawCount);
        try
        {
            var idx = DeduplicateBatchChain(comps, head, indices);

            // Copy deduplicated components into clone's batch buffer
            //
            // SAFETY NOTES (two borrow-lifetime patterns):
            //
            // 1. buf (BatchBuf): ReserveBatchBufSpace may Array.Resize(ref _frozen.BatchBuf),
            //    invalidating local buf captured before it.  Must rebind after the call.
            //    → Fixed by capturing `var buf = _frozen.BatchBuf` *after* ReserveBatchBufSpace.
            //
            // 2. ref var comp + comps (BatchComps): CommitBatchComponent may EnsureCapacity
            //    (ref _frozen.BatchComps), replacing the array.  The local `comps` and the
            //    `ref var comp` then point to the old array.  Current code is safe because:
            //    a) comp is *read* before CommitBatchComponent and *not used* after it;
            //    b) next iteration reads source entries from old comps[indices[i]] — still
            //       valid because Array.Resize copies old content and source entries are
            //       never mutated by CommitBatchComponent.
            //    **Do not** reorder or extend this loop body without considering both invalidation
            //    sources.  If you must hold comp across either call, copy its fields as values.
            for (var i = 0; i < idx; i++)
            {
                ref var comp = ref comps[indices[i]];
                var offset = ReserveBatchBufSpace(comp.Size);
                var buf = _frozen.BatchBuf;
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
        _frozen.CreateManyGroupCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
        _batchBufLen = 0;
        _lastCreated = default;
        _lastCreatedBatch = -1;
        _frozen.HierarchyByChild.Clear();
        _hasStoreCommands = false;
        _hasParallelStoreWrites = false;
        _submitEpoch = _world.ReservedReleaseEpoch;
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

    private protected struct BatchedComponent
    {
        public ComponentType Type;
        public int Offset;
        public int Size;
        public bool Removed;
        public int Next;
    }

    /// <summary>
    /// Frozen record of one CreateMany call's contiguous batch range. Captured
    /// after the per-entity pending-batch writes succeed, and consumed only by
    /// the submit fast path (<see cref="MaterializeCreateManyGroup"/>).
    /// Holds up to 8 component types inline so no per-group array is allocated.
    /// </summary>
    private protected struct CreateManyGroup
    {
        public int StartBatch;
        public int Count;
        public int ComponentCount;
        public ComponentType C1;
        public ComponentType C2;
        public ComponentType C3;
        public ComponentType C4;
        public ComponentType C5;
        public ComponentType C6;
        public ComponentType C7;
        public ComponentType C8;
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
        public CreateManyGroup[] CreateManyGroups;
        public int CreateManyGroupCount;

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
            CreateManyGroups = [];
            CreateManyGroupCount = 0;
        }

        public PendingBatchView Pending => new(
            BatchCanceled, BatchHeads, BatchCompCounts, BatchComps,
            BatchBuf, BatchEntities, PendingBatchCount);
    }

}
