using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands as a flat stream.
/// Add/Set on entities being created in the same batch are merged into the Create entry,
/// avoiding separate command entries and per-entity accumulation during Submit.
/// </summary>
public sealed class CommandStream : ICommandRecorder
{
    private readonly World _world;
    private Entry[] _entries = [];
    private int _entryCount;
    private ComponentStore?[] _stores = [];
    private Dictionary<Entity, HierarchyIntent> _hierarchyByChild = new();
    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);

    // ── Pending-entity tracking (lightweight, array-based) ───────────
    private int[] _pendingBatch = [];       // entity ID → batch index
    private int _pendingBatchCount;
    private int _pendingBatchMin = int.MaxValue; // for range check
    private int _pendingBatchMax;

    // Per-batch linked lists for component accumulation (raw bytes).
    // Each Create() gets a batch index; Add/Set on pending entities writes
    // to that batch's linked list (with last-wins per component type).
    private int[] _batchHeads = [];         // head index in _batchComps, -1 = empty
    private int[] _batchCompCounts = [];    // raw component count per batch (total, not effective)
    private BatchedComponent[] _batchComps = []; // flat node pool (linked via Next)
    private int _batchCompTotal;
    private byte[] _batchBuf = [];          // raw component data
    private int _batchBufLen;

    // Per-batch entity tracking for version-aware cancellation and
    // O(1) unavailable-entity lookup during hierarchy application.
    private Entity[] _batchEntities = [];    // entity per batch index (for version check)
    private bool[] _batchCanceled = [];      // whether batch was canceled in same frame
    private HashSet<Entity>? _unavailableEntities; // destroyed/canceled entities (O(1) lookup)

    // Local archetype cache keyed by ComponentMask.
    // Warm builds produce a handful of unique component-type combinations per scenario.
    // A small cache catches virtually all hits after the first tick.
    private const int MaskCacheSize = 8;
    private MaskCacheSlot[] _maskCache = [];
    private int _maskCacheCount;
    private int _maskCacheGeneration = -1;
    private ComponentMask _lastMask;
    private Archetype? _lastMaskArchetype;

    private readonly record struct MaskCacheSlot(ComponentMask Mask, Archetype Archetype);

    private struct BatchedComponent
    {
        public ComponentType Type;
        public int Offset;   // byte offset in _batchBuf
        public int Size;     // byte size
        public bool Removed; // marked removed by Remove<T> on pending entity
        public int Next;     // index of next component in this batch, -1 = none
    }

    public CommandStream(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    // ── Record API ────────────────────────────────────────────────────

    public Entity Create()
    {
        var entity = _world.ReserveDeferredEntity();
        var batchIdx = AllocPendingBatch(entity);
        AppendEntry(new Entry(CmdKind.Create, entity, default, batchIdx));
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
            var store = GetOrCreateStore<T>();
            var dataIndex = store.Append(component);
            AppendEntry(new Entry(CmdKind.Add, entity, CommandTypeInfo<T>.Type, dataIndex));
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
            var store = GetOrCreateStore<T>();
            var dataIndex = store.Append(component);
            AppendEntry(new Entry(CmdKind.Set, entity, CommandTypeInfo<T>.Type, dataIndex));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WritePendingComponent<T>(int batchIdx, T component) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var offset = ReserveBatchBufSpace(size);
        Unsafe.WriteUnaligned(ref _batchBuf[offset], component);
        CommitBatchComponent(batchIdx, CommandTypeInfo<T>.Type, offset, size);
    }

    /// <summary>Reserves space in the batch buffer, returns byte offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReserveBatchBufSpace(int size)
    {
        if (_batchBufLen + size > _batchBuf.Length)
            Array.Resize(ref _batchBuf, Math.Max(_batchBufLen + size, _batchBuf.Length == 0 ? 4096 : _batchBuf.Length * 2));
        var offset = _batchBufLen;
        _batchBufLen += size;
        return offset;
    }

    /// <summary>Prepends a batch component entry (O(1), no last-wins traversal).</summary>
    /// <remarks>
    /// Duplicate component types are resolved at materialization time via
    /// stable-sort + first-occurrence-kept dedup, which naturally gives
    /// last-wins semantics because prepend puts the newest value first.
    /// </remarks>
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

    public void Remove<T>(Entity entity) where T : unmanaged
    {
        if (_pendingBatchCount > 0 && TryGetPendingBatch(entity, out var batchIdx))
        {
            MarkBatchComponentRemoved(batchIdx, CommandTypeInfo<T>.Type);
        }
        else
        {
            AppendEntry(new Entry(CmdKind.Remove, entity, CommandTypeInfo<T>.Type, 0));
        }
    }

    private void MarkBatchComponentRemoved(int batchIdx, ComponentType targetType)
    {
        // Mark ALL nodes of this type as removed (there may be duplicates since
        // CommitBatchComponent does not do last-wins).
        var current = _batchHeads[batchIdx];
        while (current >= 0)
        {
            ref var comp = ref _batchComps[current];
            if (comp.Type == targetType)
                comp.Removed = true;
            current = comp.Next;
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
            AppendEntry(new Entry(CmdKind.Destroy, entity, default, 0));
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

        // Track entity per batch for version-aware cancellation checks.
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
        var id = entity.Id;
        if ((uint)(id - _pendingBatchMin) < (uint)(_pendingBatchMax - _pendingBatchMin) &&
            id < _pendingBatch.Length)
        {
            batchIdx = _pendingBatch[id];
            // Version check: same id may be reused with a different version
            // (Create→Destroy→Create in same batch). Only match if the entity
            // handle matches the one we stored at allocation time.
            if (batchIdx >= 0 && !_batchCanceled[batchIdx] && _batchEntities[batchIdx] == entity)
                return true;
        }
        batchIdx = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CancelPendingEntity(Entity entity)
    {
        var id = entity.Id;
        if (id < _pendingBatch.Length)
        {
            var batchIdx = _pendingBatch[id];
            if (batchIdx >= 0)
            {
                // Release reserved entity and disconnect the linked list
                _world.ReleaseReservedEntity(entity);
                _pendingBatch[id] = -1;
                _batchHeads[batchIdx] = -1;
                _batchCompCounts[batchIdx] = 0;
                _batchCanceled[batchIdx] = true;
                // Remove any hierarchy references for this cancelled entity
                _hierarchyByChild.Remove(entity);
            }
        }
    }

    // ── Submit ────────────────────────────────────────────────────────

    public bool Submit()
    {
        if (_entryCount == 0 && _hierarchyByChild.Count == 0 && _pendingBatchCount == 0)
            return false;

        try
        {
            ApplyAllEntries();
            ApplyHierarchy();
        }
        finally
        {
            Clear();
        }
        return true;
    }

    private void ApplyAllEntries()
    {
        // Pass 1: materialize creates, apply adds/sets/removes
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];

            switch (entry.Kind)
            {
                case CmdKind.Create:
                    MaterializePendingEntity(entry.Entity, entry.DataIndex);
                    break;

                case CmdKind.Add:
                    _stores[entry.Type.Value]!.WriteToWorld(_world, entry.Entity, entry.Type, entry.DataIndex, isAdd: true);
                    break;

                case CmdKind.Set:
                    _stores[entry.Type.Value]!.WriteToWorld(_world, entry.Entity, entry.Type, entry.DataIndex, isAdd: false);
                    break;

                case CmdKind.Remove:
                    _world.RemoveBoxed(entry.Entity, entry.Type);
                    break;
            }
        }

        // Pass 2: apply destroys (after all mutations, matching CommandBuffer order)
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.Kind == CmdKind.Destroy && _world.IsAlive(entry.Entity))
                _world.Destroy(entry.Entity);
        }
    }

    /// <summary>
    /// Resolves archetype for a ComponentMask with local caching.
    /// </summary>
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

        // Single-slot last-hit.
        if (_lastMaskArchetype is { } last && MaskEq(_lastMask, mask))
            return last;

        // Scan small direct-mapped cache.
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

        // Cache miss: mask → ComponentType[] → Signature → Archetype.
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

    private void MaterializePendingEntity(Entity entity, int batchIdx)
    {
        // Entity was cancelled (Create then Destroy in same batch) — already released
        if ((uint)batchIdx < (uint)_batchCanceled.Length && _batchCanceled[batchIdx])
            return;

        var rawCount = _batchCompCounts[batchIdx];
        if (rawCount == 0)
        {
            // No components at all — empty entity
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

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
            // Walk linked list, building bitmask + collecting unique types with last-wins.
            // Prepend order means first encounter of each type = newest value.
            ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, b7 = 0;
            var idx = 0;
            var current = _batchHeads[batchIdx];
            while (current >= 0)
            {
                ref var comp = ref _batchComps[current];
                if (!comp.Removed)
                {
                    var id = comp.Type.Value;
                    if (!HasBit(b0, b1, b2, b3, b4, b5, b6, b7, id))
                    {
                        SetBit(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, id);
                        typesFromBatch[idx] = comp.Type;
                        offsets[idx] = comp.Offset;
                        idx++;
                    }
                }
                current = comp.Next;
            }

            if (idx == 0)
            {
                _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
                return;
            }

            var mask = new ComponentMask(b0, b1, b2, b3, b4, b5, b6, b7);
            var archetype = ResolveArchetypeForMask(mask);

            unsafe
            {
                fixed (byte* ptr = _batchBuf)
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

    /// <summary>
    /// Sorts RawComponentValue[] by type (stable insertion sort) and deduplicates
    /// keeping the first occurrence (newest, due to prepend order = last-wins).
    /// Returns the unique count.
    /// </summary>
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

    /// <summary>O(1) check whether an entity is unavailable in this batch
    /// (destroyed or created-then-canceled).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEntityDestroyed(Entity entity) =>
        _unavailableEntities != null && _unavailableEntities.Contains(entity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkUnavailable(Entity entity)
    {
        _unavailableEntities ??= new HashSet<Entity>();
        _unavailableEntities.Add(entity);
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
        if (_entryCount == 0 && _hierarchyByChild.Count == 0 && _pendingBatchCount == 0)
            return Task.FromResult(new FrameDelta());

        var frozen = SwapOutState();
        var task = Task.Run(() => BuildFromFrozen(frozen));
        SubmitFromFrozen(frozen);
        return task.ContinueWith(t => t.Result.Delta,
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private FrozenState SwapOutState()
    {
        var frozen = new FrozenState
        {
            Entries = _entries,
            EntryCount = _entryCount,
            Stores = _stores,
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

        _entries = [];
        _entryCount = 0;
        _stores = [];
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
        return frozen;
    }

    private void SubmitFromFrozen(FrozenState frozen)
    {
        ApplyAllEntriesFrozen(frozen);
        ApplyHierarchyFrozen(frozen);
    }

    private void ApplyAllEntriesFrozen(FrozenState frozen)
    {
        // Pass 1: materialize creates, apply adds/sets/removes
        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];

            switch (entry.Kind)
            {
                case CmdKind.Create:
                    MaterializePendingEntityFrozen(frozen, entry.Entity, entry.DataIndex);
                    break;

                case CmdKind.Add:
                    frozen.Stores[entry.Type.Value]!.WriteToWorld(_world, entry.Entity, entry.Type, entry.DataIndex, isAdd: true);
                    break;

                case CmdKind.Set:
                    frozen.Stores[entry.Type.Value]!.WriteToWorld(_world, entry.Entity, entry.Type, entry.DataIndex, isAdd: false);
                    break;

                case CmdKind.Remove:
                    _world.RemoveBoxed(entry.Entity, entry.Type);
                    break;
            }
        }

        // Pass 2: apply destroys (after all mutations)
        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];
            if (entry.Kind == CmdKind.Destroy && _world.IsAlive(entry.Entity))
                _world.Destroy(entry.Entity);
        }
    }

    private void MaterializePendingEntityFrozen(FrozenState frozen, Entity entity, int batchIdx)
    {
        // Entity was cancelled (Create then Destroy in same batch) — already released
        if ((uint)batchIdx < (uint)frozen.BatchCanceled.Length && frozen.BatchCanceled[batchIdx])
            return;

        var rawCount = frozen.BatchCompCounts[batchIdx];
        if (rawCount == 0)
        {
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

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
            var current = frozen.BatchHeads[batchIdx];
            while (current >= 0)
            {
                ref var comp = ref frozen.BatchComps[current];
                if (!comp.Removed)
                {
                    var id = comp.Type.Value;
                    if (!HasBit(b0, b1, b2, b3, b4, b5, b6, b7, id))
                    {
                        SetBit(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7, id);
                        typesFromBatch[idx] = comp.Type;
                        offsets[idx] = comp.Offset;
                        idx++;
                    }
                }
                current = comp.Next;
            }

            if (idx == 0)
            {
                _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
                return;
            }

            var mask = new ComponentMask(b0, b1, b2, b3, b4, b5, b6, b7);
            var archetype = ResolveArchetypeForMask(mask);

            unsafe
            {
                fixed (byte* ptr = frozen.BatchBuf)
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

    private void ApplyHierarchyFrozen(FrozenState frozen)
    {
        if (frozen.HierarchyByChild.Count == 0) return;
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

    private static (FrameDelta Delta, int CopiedBytes) BuildFromFrozen(FrozenState frozen)
    {
        var delta = new FrameDelta();

        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];

            switch (entry.Kind)
            {
            case CmdKind.Create:
            {
                var batchIdx = entry.DataIndex;
                // Entity was cancelled (Create then Destroy in same batch)
                if ((uint)batchIdx < (uint)frozen.BatchCanceled.Length && frozen.BatchCanceled[batchIdx])
                {
                    delta.ReservedEntities.Add(entry.Entity);
                    delta.ReleasedEntities.Add(entry.Entity);
                    break;
                }

                delta.ReservedEntities.Add(entry.Entity);
                var rawCount = frozen.BatchCompCounts[batchIdx];
                if (rawCount == 0)
                {
                    delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                    break;
                }

                // Collect effective components from linked list (one traversal)
                var comps = new RawComponentValue[rawCount];
                var outIdx = 0;
                var current = frozen.BatchHeads[batchIdx];
                while (current >= 0)
                {
                    ref var bc = ref frozen.BatchComps[current];
                    if (!bc.Removed)
                        comps[outIdx++] = ReadRawFromBuf(frozen.BatchBuf, bc);
                    current = bc.Next;
                }

                if (outIdx == 0)
                {
                    delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                    break;
                }

                if (outIdx != comps.Length)
                    Array.Resize(ref comps, outIdx);
                if (outIdx > 1)
                    outIdx = SortAndDeduplicateComponents(comps);
                if (outIdx != comps.Length)
                    Array.Resize(ref comps, outIdx);
                delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, comps));
                break;
            }

                case CmdKind.Add:
                {
                    var store = frozen.Stores[entry.Type.Value]!;
                    var size = store.ComponentSize(entry.Type);
                    var raw = store.ReadRawCommand(entry.Entity, entry.Type, entry.DataIndex, 0, size);
                    delta.AddCommands.Add(raw);
                    break;
                }
                case CmdKind.Set:
                {
                    var store = frozen.Stores[entry.Type.Value]!;
                    var size = store.ComponentSize(entry.Type);
                    var raw = store.ReadRawCommand(entry.Entity, entry.Type, entry.DataIndex, 0, size);
                    delta.SetCommands.Add(raw);
                    break;
                }
                case CmdKind.Remove:
                    delta.RemoveCommands.Add(new RawRemoveCommand(entry.Entity, entry.Type));
                    break;

                case CmdKind.Destroy:
                    delta.DestroyedEntities.Add(entry.Entity);
                    break;
            }
        }

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            var unavailable = frozen.UnavailableEntities;
            if (unavailable != null && unavailable.Contains(child)) continue;
            if (intent.IsLinked)
            {
                if (unavailable != null && unavailable.Contains(intent.Parent)) continue;
                delta.LinkCommands.Add(new LinkCommand(intent.Parent, child));
            }
            else
            {
                delta.UnlinkCommands.Add(new UnlinkCommand(child));
            }
        }

        var copiedBytes = delta.DeepCopyOwnedData();
        return (delta, copiedBytes);
    }

    private void BuildDelta(FrameDelta delta)
    {
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];

            switch (entry.Kind)
            {
                case CmdKind.Create:
                {
                    var batchIdx = entry.DataIndex;
                    // Entity was cancelled (Create then Destroy in same batch)
                    if ((uint)batchIdx < (uint)_batchCanceled.Length && _batchCanceled[batchIdx])
                    {
                        delta.ReservedEntities.Add(entry.Entity);
                        delta.ReleasedEntities.Add(entry.Entity);
                        break;
                    }

                    delta.ReservedEntities.Add(entry.Entity);
                    var rawCount = _batchCompCounts[batchIdx];
                    if (rawCount == 0)
                    {
                        delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                        break;
                    }

                    // Collect effective components from linked list (one traversal)
                    var comps = new RawComponentValue[rawCount];
                    var outIdx = 0;
                    var current = _batchHeads[batchIdx];
                    while (current >= 0)
                    {
                        ref var bc = ref _batchComps[current];
                        if (!bc.Removed)
                            comps[outIdx++] = ReadRawFromBuf(_batchBuf, bc);
                        current = bc.Next;
                    }

                    if (outIdx == 0)
                    {
                        delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                        break;
                    }

                    if (outIdx != comps.Length)
                        Array.Resize(ref comps, outIdx);
                    if (outIdx > 1)
                        outIdx = SortAndDeduplicateComponents(comps);
                    if (outIdx != comps.Length)
                        Array.Resize(ref comps, outIdx);
                    delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, comps));
                    break;
                }

                case CmdKind.Add:
                {
                    var store = _stores[entry.Type.Value]!;
                    var size = store.ComponentSize(entry.Type);
                    var raw = store.ReadRawCommand(entry.Entity, entry.Type, entry.DataIndex, 0, size);
                    delta.AddCommands.Add(raw);
                    break;
                }
                case CmdKind.Set:
                {
                    var store = _stores[entry.Type.Value]!;
                    var size = store.ComponentSize(entry.Type);
                    var raw = store.ReadRawCommand(entry.Entity, entry.Type, entry.DataIndex, 0, size);
                    delta.SetCommands.Add(raw);
                    break;
                }
                case CmdKind.Remove:
                    delta.RemoveCommands.Add(new RawRemoveCommand(entry.Entity, entry.Type));
                    break;

                case CmdKind.Destroy:
                    delta.DestroyedEntities.Add(entry.Entity);
                    break;
            }
        }

        foreach (var (child, intent) in _hierarchyByChild)
        {
            if (IsEntityDestroyed(child)) continue;
            if (intent.IsLinked)
            {
                if (IsEntityDestroyed(intent.Parent)) continue;
                delta.LinkCommands.Add(new LinkCommand(intent.Parent, child));
            }
            else
            {
                delta.UnlinkCommands.Add(new UnlinkCommand(child));
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendEntry(Entry entry)
    {
        if (_entryCount == _entries.Length)
            Array.Resize(ref _entries, _entries.Length == 0 ? 256 : _entries.Length * 2);
        _entries[_entryCount++] = entry;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ComponentStore<T> GetOrCreateStore<T>() where T : unmanaged
    {
        var id = CommandTypeInfo<T>.Type.Value;
        if (id >= _stores.Length)
            Array.Resize(ref _stores, id + 1);

        var store = _stores[id];
        if (store == null)
        {
            store = new ComponentStore<T>();
            _stores[id] = store;
        }
        return (ComponentStore<T>)store;
    }

    private static RawComponentValue ReadRawFromBuf(byte[] buf, in BatchedComponent bc)
    {
        var bytes = new byte[bc.Size];
        Unsafe.CopyBlockUnaligned(ref bytes[0], ref buf[bc.Offset], (uint)bc.Size);
        return new RawComponentValue(bc.Type, bytes, 0, bc.Size);
    }

    private void Clear()
    {
        _entryCount = 0;
        for (var i = 0; i < _stores.Length; i++)
            _stores[i]?.Clear();

        _pendingBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
        _batchBufLen = 0;
        _hierarchyByChild.Clear();
        _unavailableEntities?.Clear();
    }

    private static class CommandTypeInfo<T> where T : unmanaged
    {
        public static readonly ComponentType Type = Component<T>.ComponentType;
    }

    // ── Internal types ────────────────────────────────────────────────

    internal enum CmdKind : byte { Create, Add, Set, Remove, Destroy }

    /// <summary>
    /// Flat command entry. For <see cref="CmdKind.Create"/> entries,
    /// <see cref="Entry.DataIndex"/> stores the pending batch index.
    /// </summary>
    internal readonly record struct Entry(
        CmdKind Kind,
        Entity Entity,
        ComponentType Type,
        int DataIndex);

    internal abstract class ComponentStore
    {
        public abstract void WriteToWorld(World world, Entity entity, ComponentType type, int dataIndex, bool isAdd);
        public abstract RawComponentCommand ReadRawCommand(Entity entity, ComponentType type, int dataIndex, int offset, int size);
        public abstract int ComponentSize(ComponentType type);
        public abstract void Clear();
    }

    private sealed class ComponentStore<T> : ComponentStore where T : unmanaged
    {
        private T[] _data = [];
        private int _count;

        public int Append(in T value)
        {
            if (_count == _data.Length)
                Array.Resize(ref _data, _data.Length == 0 ? 256 : _data.Length * 2);
            _data[_count] = value;
            return _count++;
        }

        public override void WriteToWorld(World world, Entity entity, ComponentType type, int dataIndex, bool isAdd)
        {
            if (isAdd) world.Add(entity, _data[dataIndex]);
            else world.Set(entity, _data[dataIndex]);
        }

        public override RawComponentCommand ReadRawCommand(Entity entity, ComponentType type, int dataIndex, int offset, int size)
        {
            var s = Unsafe.SizeOf<T>();
            var bytes = new byte[s];
            Unsafe.WriteUnaligned(ref bytes[0], _data[dataIndex]);
            return new RawComponentCommand(entity, type, offset, s, bytes);
        }

        public override int ComponentSize(ComponentType type) => Unsafe.SizeOf<T>();

        public override void Clear() => _count = 0;
    }

    private sealed class FrozenState
    {
        public Entry[] Entries = [];
        public int EntryCount;
        public ComponentStore?[] Stores = [];
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
    }
}
