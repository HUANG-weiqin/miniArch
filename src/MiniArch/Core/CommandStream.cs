using System.Buffers;
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
    private BatchedComponent[] _batchComps = []; // flat node pool (linked via Next)
    private int _batchCompTotal;
    private byte[] _batchBuf = [];          // raw component data
    private int _batchBufLen;

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

    public void Add<T>(Entity entity, T component)
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

    public void Set<T>(Entity entity, T component)
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
    private void WritePendingComponent<T>(int batchIdx, T component)
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
    }

    public void Remove<T>(Entity entity)
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
                if (stackCount >= stack.Length) { Array.Resize(ref stack, stack.Length * 2); Array.Resize(ref cloneStack, cloneStack.Length * 2); }
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
                    if (stackCount >= stack.Length) { Array.Resize(ref stack, stack.Length * 2); Array.Resize(ref cloneStack, cloneStack.Length * 2); }
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
        }

        var batchIdx = _pendingBatchCount++;
        _pendingBatch[entity.Id] = batchIdx;
        _batchHeads[batchIdx] = -1;

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
            return batchIdx >= 0;
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

    private void MaterializePendingEntity(Entity entity, int batchIdx)
    {
        // Entity was cancelled (Create then Destroy in same batch) — already released
        if (entity.Id < _pendingBatch.Length && _pendingBatch[entity.Id] < 0)
            return;

        var head = _batchHeads[batchIdx];
        if (head < 0)
        {
            // No components at all — empty entity
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

        // Count effective (non-removed) components by traversing the linked list
        var effectiveCount = 0;
        var current = head;
        while (current >= 0)
        {
            if (!_batchComps[current].Removed)
                effectiveCount++;
            current = _batchComps[current].Next;
        }

        if (effectiveCount == 0)
        {
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

        // Collect component types + offsets, skipping removed (zero-allocation for typical counts ≤ 64)
        ComponentType[]? pooledTypes = null;
        int[]? pooledOffsets = null;
        Span<ComponentType> typesFromBatch = effectiveCount <= 64
            ? stackalloc ComponentType[effectiveCount]
            : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(effectiveCount)).AsSpan(0, effectiveCount);
        Span<int> offsets = effectiveCount <= 64
            ? stackalloc int[effectiveCount]
            : (pooledOffsets = ArrayPool<int>.Shared.Rent(effectiveCount)).AsSpan(0, effectiveCount);
        try
        {
            var idx = 0;
            current = head;
            while (current >= 0)
            {
                ref var comp = ref _batchComps[current];
                if (!comp.Removed)
                {
                    typesFromBatch[idx] = comp.Type;
                    offsets[idx] = comp.Offset;
                    idx++;
                }
                current = comp.Next;
            }

            // Stable sort + dedup: keep first occurrence of each type.
            // Since prepend puts the newest value first and insertion sort is
            // stable, the first occurrence is the last-written value (last-wins).
            if (effectiveCount > 1)
            {
                SortTypesAndOffsets(typesFromBatch, offsets);
                effectiveCount = DeduplicateSortedSpans(typesFromBatch, offsets);
            }

            var archetype = _world.TryGetArchetype(typesFromBatch[..effectiveCount]);
            if (archetype == null)
            {
                var typeArray = typesFromBatch[..effectiveCount].ToArray();
                archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(typeArray));
            }

            unsafe
            {
                fixed (byte* ptr = _batchBuf)
                {
                    _world.MaterializeReservedEntityRaw(entity, archetype,
                        typesFromBatch[..effectiveCount], offsets[..effectiveCount], ptr);
                }
            }
        }
        finally
        {
            if (pooledTypes != null) ArrayPool<ComponentType>.Shared.Return(pooledTypes);
            if (pooledOffsets != null) ArrayPool<int>.Shared.Return(pooledOffsets);
        }
    }

    /// <summary>Sorts two parallel spans by ComponentType.Value (insertion sort, allocation-free).</summary>
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

    /// <summary>
    /// Given parallel spans already sorted by type, keeps the first occurrence
    /// of each type. Since insertion sort is stable and prepend puts the newest
    /// value first, the first occurrence = last-written = last-wins semantics.
    /// Returns the number of unique elements.
    /// </summary>
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

    private bool IsEntityDestroyed(Entity entity)
    {
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.Kind == CmdKind.Destroy && entry.Entity == entity)
                return true;
        }
        return false;
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
        return task.ContinueWith(t =>
        {
            frozen.HierarchyByChild.Clear();
            return t.Result.Delta;
        }, TaskContinuationOptions.ExecuteSynchronously);
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
            BatchComps = _batchComps,
            BatchBuf = _batchBuf,
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
        _batchComps = [];
        _batchCompTotal = 0;
        _batchBuf = [];
        _batchBufLen = 0;
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
        if (entity.Id < frozen.PendingBatch.Length && frozen.PendingBatch[entity.Id] < 0)
            return;

        var head = frozen.BatchHeads[batchIdx];
        if (head < 0)
        {
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

        // Count effective (non-removed) components
        var effectiveCount = 0;
        var current = head;
        while (current >= 0)
        {
            if (!frozen.BatchComps[current].Removed)
                effectiveCount++;
            current = frozen.BatchComps[current].Next;
        }

        if (effectiveCount == 0)
        {
            _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            return;
        }

        ComponentType[]? pooledTypes = null;
        int[]? pooledOffsets = null;
        Span<ComponentType> typesFromBatch = effectiveCount <= 64
            ? stackalloc ComponentType[effectiveCount]
            : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(effectiveCount)).AsSpan(0, effectiveCount);
        Span<int> offsets = effectiveCount <= 64
            ? stackalloc int[effectiveCount]
            : (pooledOffsets = ArrayPool<int>.Shared.Rent(effectiveCount)).AsSpan(0, effectiveCount);
        try
        {
            var idx = 0;
            current = head;
            while (current >= 0)
            {
                ref var comp = ref frozen.BatchComps[current];
                if (!comp.Removed)
                {
                    typesFromBatch[idx] = comp.Type;
                    offsets[idx] = comp.Offset;
                    idx++;
                }
                current = comp.Next;
            }

            if (effectiveCount > 1)
            {
                SortTypesAndOffsets(typesFromBatch, offsets);
                effectiveCount = DeduplicateSortedSpans(typesFromBatch, offsets);
            }

            var archetype = _world.TryGetArchetype(typesFromBatch[..effectiveCount]);
            if (archetype == null)
            {
                var typeArray = typesFromBatch[..effectiveCount].ToArray();
                archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(typeArray));
            }

            unsafe
            {
                fixed (byte* ptr = frozen.BatchBuf)
                {
                    _world.MaterializeReservedEntityRaw(entity, archetype,
                        typesFromBatch[..effectiveCount], offsets[..effectiveCount], ptr);
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
        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (IsEntityDestroyedFrozen(frozen, child)) continue;
            if (intent.IsLinked)
            {
                if (IsEntityDestroyedFrozen(frozen, intent.Parent)) continue;
                _world.Link(intent.Parent, child);
            }
            else
            {
                _world.Unlink(child);
            }
        }
    }

    private static bool IsEntityDestroyedFrozen(FrozenState frozen, Entity entity)
    {
        for (var i = 0; i < frozen.EntryCount; i++)
            if (frozen.Entries[i].Kind == CmdKind.Destroy && frozen.Entries[i].Entity == entity)
                return true;
        return false;
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
                if (entry.Entity.Id < frozen.PendingBatch.Length && frozen.PendingBatch[entry.Entity.Id] < 0)
                {
                    delta.ReservedEntities.Add(entry.Entity);
                    delta.ReleasedEntities.Add(entry.Entity);
                    break;
                }

                delta.ReservedEntities.Add(entry.Entity);
                var head = frozen.BatchHeads[batchIdx];
                if (head < 0)
                {
                    delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                    break;
                }

                // Collect effective components from linked list
                var effectiveCount = 0;
                var current = head;
                while (current >= 0)
                {
                    if (!frozen.BatchComps[current].Removed)
                        effectiveCount++;
                    current = frozen.BatchComps[current].Next;
                }

                if (effectiveCount == 0)
                {
                    delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                    break;
                }

                var comps = new RawComponentValue[effectiveCount];
                var outIdx = 0;
                current = head;
                while (current >= 0)
                {
                    ref var bc = ref frozen.BatchComps[current];
                    if (!bc.Removed)
                        comps[outIdx++] = ReadRawFromBuf(frozen.BatchBuf, bc);
                    current = bc.Next;
                }
                if (effectiveCount > 1)
                    effectiveCount = SortAndDeduplicateComponents(comps);
                if (effectiveCount != comps.Length)
                    Array.Resize(ref comps, effectiveCount);
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
            if (IsEntityDestroyedFrozen(frozen, child)) continue;
            if (intent.IsLinked)
            {
                if (IsEntityDestroyedFrozen(frozen, intent.Parent)) continue;
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
                    if (entry.Entity.Id < _pendingBatch.Length && _pendingBatch[entry.Entity.Id] < 0)
                    {
                        delta.ReservedEntities.Add(entry.Entity);
                        delta.ReleasedEntities.Add(entry.Entity);
                        break;
                    }

                    delta.ReservedEntities.Add(entry.Entity);
                    var head = _batchHeads[batchIdx];
                    if (head < 0)
                    {
                        delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                        break;
                    }

                    // Collect effective components from linked list
                    var effectiveCount = 0;
                    var current = head;
                    while (current >= 0)
                    {
                        if (!_batchComps[current].Removed)
                            effectiveCount++;
                        current = _batchComps[current].Next;
                    }

                    if (effectiveCount == 0)
                    {
                        delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, []));
                        break;
                    }

                    var comps = new RawComponentValue[effectiveCount];
                    var outIdx = 0;
                    current = head;
                    while (current >= 0)
                    {
                        ref var bc = ref _batchComps[current];
                        if (!bc.Removed)
                            comps[outIdx++] = ReadRawFromBuf(_batchBuf, bc);
                        current = bc.Next;
                    }
                    if (effectiveCount > 1)
                        effectiveCount = SortAndDeduplicateComponents(comps);
                    if (effectiveCount != comps.Length)
                        Array.Resize(ref comps, effectiveCount);
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
    private ComponentStore<T> GetOrCreateStore<T>()
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

        // Clear pending batch tracking
        for (var i = _pendingBatchMin; i < _pendingBatchMax && i < _pendingBatch.Length; i++)
            _pendingBatch[i] = -1;
        // Disconnect all linked lists (nodes remain in pool, overwritten next frame)
        for (var i = 0; i < _pendingBatchCount; i++)
            _batchHeads[i] = -1;
        _pendingBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
        _batchBufLen = 0;
        _hierarchyByChild.Clear();
    }

    private static class CommandTypeInfo<T>
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

    private sealed class ComponentStore<T> : ComponentStore
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
        public BatchedComponent[] BatchComps = [];
        public byte[] BatchBuf = [];
    }
}
