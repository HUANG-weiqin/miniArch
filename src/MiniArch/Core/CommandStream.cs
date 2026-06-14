using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands with per-component-type append-only stores.
/// Add/Set/Remove on existing entities are stored inline in typed arrays — no entry stream,
/// no per-entity dedup. Created entities use a pending batch buffer for pre-materialization
/// component accumulation.
/// </summary>
public sealed class CommandStream : ICommandRecorder
{
    private readonly World _world;
    private ComponentStore?[] _stores;

    // ── Entity-level commands ──────────────────────────────────────────
    private Entity[] _destroyEntities = [];
    private int _destroyCount;

    // ── Hierarchy ──────────────────────────────────────────────────────
    private Dictionary<Entity, HierarchyIntent> _hierarchyByChild = new();

    // ── Pending-entity tracking ────────────────────────────────────────
    private int[] _pendingBatch = [];
    private int _pendingBatchCount;
    private int _pendingBatchMin = int.MaxValue;
    private int _pendingBatchMax;

    // Per-batch linked lists for component accumulation.
    private int[] _batchHeads = [];
    private int[] _batchCompCounts = [];
    private BatchedComponent[] _batchComps = [];
    private int _batchCompTotal;
    private byte[] _batchBuf = [];
    private int _batchBufLen;

    // Per-batch entity tracking for version-aware cancellation.
    private Entity[] _batchEntities = [];
    private bool[] _batchCanceled = [];
    private HashSet<Entity>? _unavailableEntities;

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

    // ── Construction ───────────────────────────────────────────────────

    public CommandStream(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _stores = new ComponentStore?[ComponentRegistry.Shared.ComponentTypeCount];
    }

    // ── Record API ────────────────────────────────────────────────────

    public Entity Create()
    {
        var entity = _world.ReserveDeferredEntity();
        var batchIdx = AllocPendingBatch(entity);
        _lastCreated = entity;
        _lastCreatedBatch = batchIdx;
        return entity;
    }

    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        if (_pendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else
        {
            GetOrCreateStore<T>().Append(entity, component, KindAdd);
        }
    }

    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        if (_pendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            WritePendingComponent(batchIdx, component);
        }
        else
        {
            GetOrCreateStore<T>().Append(entity, component, KindSet);
        }
    }

    public void Remove<T>(Entity entity) where T : unmanaged
    {
        if (_pendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        }
        else
        {
            GetOrCreateStore<T>().AppendRemove(entity);
        }
    }

    public void Destroy(Entity entity)
    {
        MarkUnavailable(entity);
        if (TryGetPendingBatch(entity, out _))
        {
            CancelPendingEntity(entity);
        }
        else
        {
            AppendDestroy(entity);
        }
    }

    public void Link(Entity parent, Entity child)
    {
        _hierarchyByChild[child] = new HierarchyIntent(true, parent);
    }

    public void Unlink(Entity child)
    {
        _hierarchyByChild[child] = new HierarchyIntent(false, default);
    }

    public Entity Clone(Entity source)
    {
        if (!_world.TryGetLocation(source, out var location))
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

        var clone = Create();
        var batchIdx = _pendingBatch[clone.Id];
        var archetype = location.Archetype;
        var sourceRow = location.RowIndex;
        var components = archetype.Signature.AsSpan();

        for (var i = 0; i < components.Length; i++)
        {
            var ct = components[i];
            var size = ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(ct));
            var offset = ReserveBatchBufSpace(size);
            unsafe
            {
                fixed (byte* ptr = &_batchBuf[offset])
                    archetype.ReadComponentRaw(i, sourceRow, ptr);
            }
            CommitBatchComponent(batchIdx, ct, offset, size);
        }

        CloneChildrenRecursive(source, clone);
        return clone;
    }

    // ── Submit ────────────────────────────────────────────────────────

    public bool Submit()
    {
        if (!HasAnyCommands())
            return false;

        try
        {
            MaterializeAllPending();
            ApplyComponentStores();
            ApplyDestroys();
            ApplyHierarchy();
        }
        finally
        {
            Clear();
        }
        return true;
    }

    private bool HasAnyCommands()
    {
        if (_pendingBatchCount > 0 || _destroyCount > 0 || _hierarchyByChild.Count > 0)
            return true;
        for (var i = 0; i < _stores.Length; i++)
            if (_stores[i]?.HasCommands == true)
                return true;
        return false;
    }

    private void MaterializeAllPending()
    {
        for (var i = 0; i < _pendingBatchCount; i++)
        {
            if (_batchCanceled[i]) continue;
            var entity = _batchEntities[i];
            MaterializePendingEntity(entity, i);
        }
    }

    private void ApplyComponentStores()
    {
        foreach (var store in _stores)
        {
            if (store?.HasCommands == true)
                store.ApplyToWorld(_world);
        }
    }

    private void ApplyDestroys()
    {
        for (var i = 0; i < _destroyCount; i++)
        {
            var entity = _destroyEntities[i];
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }
    }

    private void ApplyHierarchy()
    {
        if (_hierarchyByChild.Count == 0) return;

        foreach (var (child, intent) in _hierarchyByChild)
        {
            if (IsEntityDestroyed(child)) continue;

            if (intent.IsLinked)
            {
                if (IsEntityDestroyed(intent.Parent)) continue;
                _world.Link(intent.Parent, child);
            }
            else
            {
                _world.Unlink(child);
            }
        }
    }

    // ── Snapshot / SubmitAndSnapshotAsync ─────────────────────────────

    public FrameDelta Snapshot()
    {
        var delta = new FrameDelta();
        BuildDelta(delta);
        delta.DeepCopyOwnedData();
        return delta;
    }

    public Task<FrameDelta> SubmitAndSnapshotAsync()
    {
        if (!HasAnyCommands())
            return Task.FromResult(new FrameDelta());

        var frozen = SwapOutState();
        var task = Task.Run(() => BuildFromFrozen(frozen));
        SubmitFromFrozen(frozen);
        return task;
    }

    private void BuildDelta(FrameDelta delta)
    {
        EmitPendingEntitiesToDelta(delta, new PendingBatchView(
            _batchCanceled, _batchHeads, _batchCompCounts,
            _batchComps, _batchBuf, _batchEntities, _pendingBatchCount));

        foreach (var store in _stores)
        {
            if (store?.HasCommands == true)
                store.EmitToDelta(delta);
        }

        for (var i = 0; i < _destroyCount; i++)
            delta.DestroyedEntities.Add(_destroyEntities[i]);

        EmitHierarchyToDelta(delta, _hierarchyByChild, _unavailableEntities);
    }

    private FrozenState SwapOutState()
    {
        var frozen = new FrozenState
        {
            Stores = _stores,
            DestroyEntities = _destroyEntities,
            DestroyCount = _destroyCount,
            HierarchyByChild = _hierarchyByChild,
            PendingBatch = _pendingBatch,
            PendingBatchCount = _pendingBatchCount,
            BatchHeads = _batchHeads,
            BatchCompCounts = _batchCompCounts,
            BatchComps = _batchComps,
            BatchBuf = _batchBuf,
            UnavailableEntities = _unavailableEntities,
            BatchEntities = _batchEntities,
            BatchCanceled = _batchCanceled,
        };

        _stores = new ComponentStore?[ComponentRegistry.Shared.ComponentTypeCount];
        _destroyEntities = [];
        _destroyCount = 0;
        _hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        _pendingBatch = [];
        _pendingBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchHeads = [];
        _batchCompCounts = [];
        _batchComps = [];
        _batchCompTotal = 0;
        _batchBuf = [];
        _batchBufLen = 0;
        _batchEntities = [];
        _batchCanceled = [];
        _unavailableEntities = null;
        _lastCreatedBatch = -1;
        return frozen;
    }

    private void SubmitFromFrozen(FrozenState frozen)
    {
        // Materialize pending
        for (var i = 0; i < frozen.PendingBatchCount; i++)
        {
            if (frozen.BatchCanceled[i]) continue;
            MaterializePendingEntityFrozen(frozen, frozen.BatchEntities[i], i);
        }

        // Apply component stores
        foreach (var store in frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.ApplyToWorld(_world);
        }

        // Apply destroys
        for (var i = 0; i < frozen.DestroyCount; i++)
        {
            var entity = frozen.DestroyEntities[i];
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }

        // Apply hierarchy
        if (frozen.HierarchyByChild.Count > 0)
        {
            var unavailable = frozen.UnavailableEntities;
            foreach (var (child, intent) in frozen.HierarchyByChild)
            {
                if (unavailable != null && unavailable.Contains(child)) continue;
                if (intent.IsLinked)
                {
                    if (unavailable != null && unavailable.Contains(intent.Parent)) continue;
                    _world.Link(intent.Parent, child);
                }
                else
                {
                    _world.Unlink(child);
                }
            }
        }
    }

    private static FrameDelta BuildFromFrozen(FrozenState frozen)
    {
        var delta = new FrameDelta();

        EmitPendingEntitiesToDelta(delta, frozen.Pending);

        foreach (var store in frozen.Stores)
        {
            if (store?.HasCommands == true)
                store.EmitToDelta(delta);
        }

        for (var i = 0; i < frozen.DestroyCount; i++)
            delta.DestroyedEntities.Add(frozen.DestroyEntities[i]);

        EmitHierarchyToDelta(delta, frozen.HierarchyByChild, frozen.UnavailableEntities);

        delta.DeepCopyOwnedData();
        return delta;
    }

    // ── Pending entity materialization ─────────────────────────────────

    private void MaterializePendingEntity(Entity entity, int batchIdx)
    {
        if ((uint)batchIdx < (uint)_batchCanceled.Length && _batchCanceled[batchIdx])
            return;

        var rawCount = _batchCompCounts[batchIdx];
        if (rawCount == 0)
        {
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

        MaterializeFromBatchBuffer(entity, _batchHeads[batchIdx], _batchComps, _batchBuf, rawCount);
    }

    private void MaterializePendingEntityFrozen(FrozenState frozen, Entity entity, int batchIdx)
    {
        if ((uint)batchIdx < (uint)frozen.BatchCanceled.Length && frozen.BatchCanceled[batchIdx])
            return;

        var rawCount = frozen.BatchCompCounts[batchIdx];
        if (rawCount == 0)
        {
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

        MaterializeFromBatchBuffer(entity, frozen.BatchHeads[batchIdx], frozen.BatchComps, frozen.BatchBuf, rawCount);
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
                        if (!HasBit(b0, b1, b2, b3, b4, b5, b6, b7, id))
                        {
                            SetBit(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, id);
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
                _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
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

    private static void EmitPendingEntitiesToDelta(FrameDelta delta, in PendingBatchView view)
    {
        var batchCanceled = view.Canceled;
        var batchHeads = view.Heads;
        var batchCompCounts = view.CompCounts;
        var batchComps = view.Comps;
        var batchBuf = view.Buf;
        var batchEntities = view.Entities;
        var pendingBatchCount = view.Count;

        for (var i = 0; i < pendingBatchCount; i++)
        {
            var entity = batchEntities[i];
            if ((uint)i < (uint)batchCanceled.Length && batchCanceled[i])
            {
                delta.ReservedEntities.Add(entity);
                delta.ReleasedEntities.Add(entity);
                continue;
            }

            delta.ReservedEntities.Add(entity);
            var rawCount = batchCompCounts[i];
            if (rawCount == 0)
            {
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, []));
                continue;
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
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, []));
                continue;
            }

            if (outIdx != comps.Length)
                Array.Resize(ref comps, outIdx);
            if (outIdx > 1)
                outIdx = SortAndDeduplicateComponents(comps);
            if (outIdx != comps.Length)
                Array.Resize(ref comps, outIdx);
            delta.CreatedEntities.Add(new RawCreatedEntity(entity, comps));
        }
    }

    private static void EmitHierarchyToDelta(FrameDelta delta,
        Dictionary<Entity, HierarchyIntent> hierarchyByChild,
        HashSet<Entity>? unavailableEntities)
    {
        if (hierarchyByChild.Count == 0) return;

        foreach (var (child, intent) in hierarchyByChild)
        {
            if (unavailableEntities != null && unavailableEntities.Contains(child)) continue;
            if (intent.IsLinked)
            {
                if (unavailableEntities != null && unavailableEntities.Contains(intent.Parent)) continue;
                delta.LinkCommands.Add(new LinkCommand(intent.Parent, child));
            }
            else
            {
                delta.UnlinkCommands.Add(new UnlinkCommand(child));
            }
        }
    }

    // ── Pending entity helpers ────────────────────────────────────────

    private int AllocPendingBatch(Entity entity)
    {
        if (entity.Id >= _pendingBatch.Length)
        {
            var newLen = _pendingBatch.Length == 0 ? 64 : _pendingBatch.Length;
            while (newLen <= entity.Id) newLen *= 2;
            var next = new int[newLen];
            Array.Fill(next, -1);
            if (_pendingBatch.Length > 0)
                Array.Copy(_pendingBatch, next, _pendingBatch.Length);
            _pendingBatch = next;
        }

        if (_pendingBatchCount == _batchHeads.Length)
        {
            var newSize = _batchHeads.Length == 0 ? 16 : _batchHeads.Length * 2;
            Array.Resize(ref _batchHeads, newSize);
            Array.Resize(ref _batchCompCounts, newSize);
        }

        var batchIdx = _pendingBatchCount++;
        _pendingBatch[entity.Id] = batchIdx;
        _batchHeads[batchIdx] = -1;
        _batchCompCounts[batchIdx] = 0;

        if (batchIdx >= _batchEntities.Length)
            Array.Resize(ref _batchEntities, _batchHeads.Length);
        if (batchIdx >= _batchCanceled.Length)
            Array.Resize(ref _batchCanceled, _batchHeads.Length);
        _batchEntities[batchIdx] = entity;
        _batchCanceled[batchIdx] = false;

        if (entity.Id < _pendingBatchMin) _pendingBatchMin = entity.Id;
        if (entity.Id >= _pendingBatchMax) _pendingBatchMax = entity.Id + 1;

        return batchIdx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetPendingBatch(Entity entity, out int batchIdx)
    {
        if (entity == _lastCreated && _lastCreatedBatch >= 0)
        {
            batchIdx = _lastCreatedBatch;
            return true;
        }

        var id = entity.Id;
        if ((uint)(id - _pendingBatchMin) < (uint)(_pendingBatchMax - _pendingBatchMin) &&
            id < _pendingBatch.Length)
        {
            batchIdx = _pendingBatch[id];
            if (batchIdx >= 0 && !_batchCanceled[batchIdx] && _batchEntities[batchIdx] == entity)
                return true;
        }
        batchIdx = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CancelPendingEntity(Entity entity)
    {
        if (entity == _lastCreated)
            _lastCreatedBatch = -1;

        var id = entity.Id;
        if (id < _pendingBatch.Length)
        {
            var batchIdx = _pendingBatch[id];
            if (batchIdx >= 0)
            {
                _world.ReleaseReservedEntity(entity);
                _pendingBatch[id] = -1;
                _batchHeads[batchIdx] = -1;
                _batchCompCounts[batchIdx] = 0;
                _batchCanceled[batchIdx] = true;
                _hierarchyByChild.Remove(entity);
            }
        }
    }

    // ── Batch buffer helpers ──────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePendingComponent<T>(int batchIdx, T component) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var offset = ReserveBatchBufSpace(size);
        Unsafe.WriteUnaligned(ref _batchBuf[offset], component);
        CommitBatchComponent(batchIdx, CommandTypeInfo<T>.Type, offset, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReserveBatchBufSpace(int size)
    {
        if (_batchBufLen + size > _batchBuf.Length)
            Array.Resize(ref _batchBuf, Math.Max(_batchBufLen + size, _batchBuf.Length == 0 ? 4096 : _batchBuf.Length * 2));
        var offset = _batchBufLen;
        _batchBufLen += size;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitBatchComponent(int batchIdx, ComponentType type, int offset, int size)
    {
        if (_batchCompTotal == _batchComps.Length)
            Array.Resize(ref _batchComps, _batchComps.Length == 0 ? 256 : _batchComps.Length * 2);
        _batchComps[_batchCompTotal] = new BatchedComponent
        {
            Type = type,
            Offset = offset,
            Size = size,
            Next = _batchHeads[batchIdx],
        };
        _batchHeads[batchIdx] = _batchCompTotal;
        _batchCompTotal++;
        _batchCompCounts[batchIdx]++;
    }

    private void MarkBatchComponentRemoved(int batchIdx, ComponentType targetType)
    {
        var current = _batchHeads[batchIdx];
        while (current >= 0)
        {
            ref var comp = ref _batchComps[current];
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

        if (_lastMaskArchetype is { } last && MaskEq(_lastMask, mask))
            return last;

        for (var i = 0; i < _maskCacheCount; i++)
        {
            ref var slot = ref _maskCache[i];
            if (MaskEq(slot.Mask, mask))
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
    private static bool MaskEq(ComponentMask a, ComponentMask b) =>
        a.B0 == b.B0 && a.B1 == b.B1 && a.B2 == b.B2 && a.B3 == b.B3 &&
        a.B4 == b.B4 && a.B5 == b.B5 && a.B6 == b.B6 && a.B7 == b.B7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasBit(ulong b0, ulong b1, ulong b2, ulong b3,
                               ulong b4, ulong b5, ulong b6, ulong b7, int id)
    {
        if (id < 64)      return (b0 & (1UL << id)) != 0;
        if (id < 128)     return (b1 & (1UL << (id - 64))) != 0;
        if (id < 192)     return (b2 & (1UL << (id - 128))) != 0;
        if (id < 256)     return (b3 & (1UL << (id - 192))) != 0;
        if (id < 320)     return (b4 & (1UL << (id - 256))) != 0;
        if (id < 384)     return (b5 & (1UL << (id - 320))) != 0;
        if (id < 448)     return (b6 & (1UL << (id - 384))) != 0;
        return (b7 & (1UL << (id - 448))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetBit(ref ulong b0, ref ulong b1, ref ulong b2, ref ulong b3,
                               ref ulong b4, ref ulong b5, ref ulong b6, ref ulong b7, int id)
    {
        if (id < 64)      { b0 |= 1UL << id; return; }
        if (id < 128)     { b1 |= 1UL << (id - 64); return; }
        if (id < 192)     { b2 |= 1UL << (id - 128); return; }
        if (id < 256)     { b3 |= 1UL << (id - 192); return; }
        if (id < 320)     { b4 |= 1UL << (id - 256); return; }
        if (id < 384)     { b5 |= 1UL << (id - 320); return; }
        if (id < 448)     { b6 |= 1UL << (id - 384); return; }
        b7 |= 1UL << (id - 448);
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
                var batchIdx = _pendingBatch[cloneChild.Id];
                var archetype = childLocation.Archetype;
                var sourceRow = childLocation.RowIndex;
                var sig = archetype.Signature.AsSpan();
                for (var i = 0; i < sig.Length; i++)
                {
                    var ct = sig[i];
                    var size = ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(ct));
                    var offset = ReserveBatchBufSpace(size);
                    unsafe
                    {
                        fixed (byte* ptr = &_batchBuf[offset])
                            archetype.ReadComponentRaw(i, sourceRow, ptr);
                    }
                    CommitBatchComponent(batchIdx, ct, offset, size);
                }
                Link(cloneParent, cloneChild);

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
        if (_destroyCount == _destroyEntities.Length)
        {
            var newLen = _destroyEntities.Length == 0 ? 64 : _destroyEntities.Length * 2;
            Array.Resize(ref _destroyEntities, newLen);
        }
        _destroyEntities[_destroyCount++] = entity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEntityDestroyed(Entity entity) =>
        _unavailableEntities != null && _unavailableEntities.Contains(entity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkUnavailable(Entity entity)
    {
        _unavailableEntities ??= new HashSet<Entity>();
        _unavailableEntities.Add(entity);
    }

    // ── Store management ──────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentStore<T> GetOrCreateStore<T>() where T : unmanaged
    {
        var id = CommandTypeInfo<T>.Type.Value;
        if ((uint)id >= (uint)_stores.Length)
            Array.Resize(ref _stores, id + 1);

        var store = _stores[id];
        if (store == null)
        {
            store = new ComponentStore<T>();
            _stores[id] = store;
        }
        return (ComponentStore<T>)store;
    }

    // ── Clear ─────────────────────────────────────────────────────────

    private void Clear()
    {
        foreach (var store in _stores)
            store?.Clear();

        _destroyCount = 0;
        _pendingBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
        _batchBufLen = 0;
        _lastCreatedBatch = -1;
        _hierarchyByChild.Clear();
        _unavailableEntities?.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static RawComponentValue ReadRawFromBuf(byte[] buf, in BatchedComponent bc)
    {
        var bytes = new byte[bc.Size];
        Unsafe.CopyBlockUnaligned(ref bytes[0], ref buf[bc.Offset], (uint)bc.Size);
        return new RawComponentValue(bc.Type, bytes, 0, bc.Size);
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
    }

    private sealed class ComponentStore<T> : ComponentStore where T : unmanaged
    {
        private T[] _data = [];
        private Entity[] _entities = [];
        private byte[] _kinds = [];
        private int _count;

        public override bool HasCommands => _count > 0;

        public void Append(Entity entity, in T value, byte kind)
        {
            if (_count == _data.Length)
            {
                var newLen = _data.Length == 0 ? 256 : _data.Length * 2;
                Array.Resize(ref _data, newLen);
                Array.Resize(ref _entities, newLen);
                Array.Resize(ref _kinds, newLen);
            }
            _entities[_count] = entity;
            _data[_count] = value;
            _kinds[_count] = kind;
            _count++;
        }

        public void AppendRemove(Entity entity)
        {
            if (_count == _data.Length)
            {
                var newLen = _data.Length == 0 ? 256 : _data.Length * 2;
                Array.Resize(ref _data, newLen);
                Array.Resize(ref _entities, newLen);
                Array.Resize(ref _kinds, newLen);
            }
            _entities[_count] = entity;
            _data[_count] = default;
            _kinds[_count] = KindRemove;
            _count++;
        }

        public override void ApplyToWorld(World world)
        {
            for (var i = 0; i < _count; i++)
            {
                if (!world.IsAlive(_entities[i])) continue;
                switch (_kinds[i])
                {
                    case KindAdd:
                        world.Add(_entities[i], _data[i]);
                        break;
                    case KindSet:
                        world.Set(_entities[i], _data[i]);
                        break;
                    case KindRemove:
                        world.RemoveBoxed(_entities[i], Component<T>.ComponentType);
                        break;
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
                    case KindSet:
                    {
                        var bytes = new byte[size];
                        Unsafe.WriteUnaligned(ref bytes[0], _data[i]);
                        var cmd = new RawComponentCommand(_entities[i], compType, 0, size, bytes);
                        if (_kinds[i] == KindAdd)
                            delta.AddCommands.Add(cmd);
                        else
                            delta.SetCommands.Add(cmd);
                        break;
                    }
                    case KindRemove:
                        delta.RemoveCommands.Add(new RawRemoveCommand(_entities[i], compType));
                        break;
                }
            }
        }

        public override void Clear() => _count = 0;
    }

    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);

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
        public ComponentStore?[] Stores = [];
        public Entity[] DestroyEntities = [];
        public int DestroyCount;
        public Dictionary<Entity, HierarchyIntent> HierarchyByChild = new();
        public int[] PendingBatch = [];
        public int PendingBatchCount;
        public int[] BatchHeads = [];
        public int[] BatchCompCounts = [];
        public BatchedComponent[] BatchComps = [];
        public byte[] BatchBuf = [];
        public HashSet<Entity>? UnavailableEntities;
        public Entity[] BatchEntities = [];
        public bool[] BatchCanceled = [];

        public PendingBatchView Pending => new(
            BatchCanceled, BatchHeads, BatchCompCounts, BatchComps,
            BatchBuf, BatchEntities, PendingBatchCount);
    }
}
