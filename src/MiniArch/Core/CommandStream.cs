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

    // Per-batch component accumulation
    private int[] _batchCompCounts = [];    // component count per batch
    private BatchedComponent[] _batchComps = []; // flat component list
    private int _batchCompTotal;

    private struct BatchedComponent
    {
        public ComponentType Type;
        public int DataIndex;
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
        var store = GetOrCreateStore<T>();
        var dataIndex = store.Append(component);

        if (TryGetPendingBatch(entity, out var batchIdx))
        {
            AppendBatchComponent(batchIdx, CommandTypeInfo<T>.Type, dataIndex);
        }
        else
        {
            AppendEntry(new Entry(CmdKind.Add, entity, CommandTypeInfo<T>.Type, dataIndex));
        }
    }

    public void Set<T>(Entity entity, T component)
    {
        var store = GetOrCreateStore<T>();
        var dataIndex = store.Append(component);

        if (TryGetPendingBatch(entity, out var batchIdx))
        {
            AppendBatchComponent(batchIdx, CommandTypeInfo<T>.Type, dataIndex);
        }
        else
        {
            AppendEntry(new Entry(CmdKind.Set, entity, CommandTypeInfo<T>.Type, dataIndex));
        }
    }

    public void Remove<T>(Entity entity)
    {
        // If pending, mark component for removal from batch (simplification: cancel whole entity)
        // For correctness in typical usage (no partial removes on pending entities), just cancel.
        if (TryGetPendingBatch(entity, out _))
        {
            // Cancel the pending creation — destroy will release the reserved entity later
        }
        else
        {
            AppendEntry(new Entry(CmdKind.Remove, entity, CommandTypeInfo<T>.Type, 0));
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
            var store = GetOrCreateStore(ct);
            var dataIndex = store.AppendFromArchetype(archetype, i, sourceRow);
            AppendBatchComponent(batchIdx, ct, dataIndex);
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
                    var store = GetOrCreateStore(ct);
                    var dataIndex = store.AppendFromArchetype(archetype, i, sourceRow);
                    AppendBatchComponent(batchIdx, ct, dataIndex);
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

        if (_pendingBatchCount == _batchCompCounts.Length)
        {
            var newSize = _batchCompCounts.Length == 0 ? 16 : _batchCompCounts.Length * 2;
            Array.Resize(ref _batchCompCounts, newSize);
        }

        var batchIdx = _pendingBatchCount++;
        _pendingBatch[entity.Id] = batchIdx;
        _batchCompCounts[batchIdx] = 0;

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
    private void AppendBatchComponent(int batchIdx, ComponentType type, int dataIndex)
    {
        if (_batchCompTotal == _batchComps.Length)
            Array.Resize(ref _batchComps, _batchComps.Length == 0 ? 256 : _batchComps.Length * 2);

        _batchComps[_batchCompTotal++] = new BatchedComponent { Type = type, DataIndex = dataIndex };
        _batchCompCounts[batchIdx]++;
    }

    private void CancelPendingEntity(Entity entity)
    {
        var id = entity.Id;
        if (id < _pendingBatch.Length)
        {
            var batchIdx = _pendingBatch[id];
            if (batchIdx >= 0)
            {
                // Release reserved entity and mark batch as cancelled
                _world.ReleaseReservedEntity(entity);
                _pendingBatch[id] = -1;
                // Clear component count so materialization is skipped
                _batchCompCounts[batchIdx] = 0;
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
            // Precompute batch component start offsets
            var batchStarts = _pendingBatchCount == 0
                ? []
                : ArrayPool<int>.Shared.Rent(_pendingBatchCount + 1);
            if (_pendingBatchCount > 0)
            {
                batchStarts[0] = 0;
                for (var i = 0; i < _pendingBatchCount; i++)
                    batchStarts[i + 1] = batchStarts[i] + _batchCompCounts[i];
            }

            ApplyAllEntries(batchStarts);
            ApplyHierarchy();

            if (batchStarts.Length > 0)
                ArrayPool<int>.Shared.Return(batchStarts);
        }
        finally
        {
            Clear();
        }
        return true;
    }

    private void ApplyAllEntries(int[] batchStarts)
    {
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];

            switch (entry.Kind)
            {
                case CmdKind.Create:
                    MaterializePendingEntity(entry.Entity, entry.DataIndex, batchStarts);
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

                case CmdKind.Destroy:
                    if (_world.IsAlive(entry.Entity))
                        _world.Destroy(entry.Entity);
                    break;
            }
        }
    }

    private void MaterializePendingEntity(Entity entity, int batchIdx, int[] batchStarts)
    {
        var count = _batchCompCounts[batchIdx];
        if (count == 0) return;

        // Collect component types (zero-allocation for typical counts ≤ 64)
        ComponentType[]? pooledTypes = null;
        Span<ComponentType> typesFromBatch = count <= 64
            ? stackalloc ComponentType[count]
            : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            for (var c = 0; c < count; c++)
                typesFromBatch[c] = _batchComps[batchStarts[batchIdx] + c].Type;

            var archetype = _world.TryGetArchetype(typesFromBatch);
            if (archetype == null)
            {
                var typeArray = typesFromBatch.ToArray();
                archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(typeArray));
            }

            var refs = ArrayPool<ComponentRef>.Shared.Rent(count);
            for (var c = 0; c < count; c++)
            {
                ref var comp = ref _batchComps[batchStarts[batchIdx] + c];
                refs[c] = new ComponentRef(comp.Type, _stores[comp.Type.Value]!, comp.DataIndex);
            }

            _world.MaterializeReservedEntityTyped(entity, archetype, new ReadOnlySpan<ComponentRef>(refs, 0, count));
            ArrayPool<ComponentRef>.Shared.Return(refs);
        }
        finally
        {
            if (pooledTypes != null)
                ArrayPool<ComponentType>.Shared.Return(pooledTypes);
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
            PendingBatchMin = _pendingBatchMin,
            PendingBatchMax = _pendingBatchMax,
            BatchCompCounts = _batchCompCounts,
            BatchComps = _batchComps,
            BatchCompTotal = _batchCompTotal,
        };

        _entries = [];
        _entryCount = 0;
        _stores = [];
        _hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        _pendingBatch = [];
        _pendingBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompCounts = [];
        _batchComps = [];
        _batchCompTotal = 0;
        return frozen;
    }

    private void SubmitFromFrozen(FrozenState frozen)
    {
        var batchStarts = frozen.PendingBatchCount == 0
            ? []
            : ArrayPool<int>.Shared.Rent(frozen.PendingBatchCount + 1);
        if (frozen.PendingBatchCount > 0)
        {
            batchStarts[0] = 0;
            for (var i = 0; i < frozen.PendingBatchCount; i++)
                batchStarts[i + 1] = batchStarts[i] + frozen.BatchCompCounts[i];
        }

        try
        {
            ApplyAllEntriesFrozen(frozen, batchStarts);
            ApplyHierarchyFrozen(frozen);
        }
        finally
        {
            if (batchStarts.Length > 0)
                ArrayPool<int>.Shared.Return(batchStarts);
        }
    }

    private void ApplyAllEntriesFrozen(FrozenState frozen, int[] batchStarts)
    {
        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];

            switch (entry.Kind)
            {
                case CmdKind.Create:
                    MaterializePendingEntityFrozen(frozen, entry.Entity, entry.DataIndex, batchStarts);
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

                case CmdKind.Destroy:
                    if (_world.IsAlive(entry.Entity))
                        _world.Destroy(entry.Entity);
                    break;
            }
        }
    }

    private void MaterializePendingEntityFrozen(FrozenState frozen, Entity entity, int batchIdx, int[] batchStarts)
    {
        var count = frozen.BatchCompCounts[batchIdx];
        if (count == 0) return;

        ComponentType[]? pooledTypes = null;
        Span<ComponentType> typesFromBatch = count <= 64
            ? stackalloc ComponentType[count]
            : (pooledTypes = ArrayPool<ComponentType>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            for (var c = 0; c < count; c++)
                typesFromBatch[c] = frozen.BatchComps[batchStarts[batchIdx] + c].Type;

            var archetype = _world.TryGetArchetype(typesFromBatch);
            if (archetype == null)
            {
                var typeArray = typesFromBatch.ToArray();
                archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(typeArray));
            }

            var refs = ArrayPool<ComponentRef>.Shared.Rent(count);
            for (var c = 0; c < count; c++)
            {
                ref var comp = ref frozen.BatchComps[batchStarts[batchIdx] + c];
                refs[c] = new ComponentRef(comp.Type, frozen.Stores[comp.Type.Value]!, comp.DataIndex);
            }

            _world.MaterializeReservedEntityTyped(entity, archetype, new ReadOnlySpan<ComponentRef>(refs, 0, count));
            ArrayPool<ComponentRef>.Shared.Return(refs);
        }
        finally
        {
            if (pooledTypes != null)
                ArrayPool<ComponentType>.Shared.Return(pooledTypes);
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

        var batchStarts = frozen.PendingBatchCount == 0
            ? []
            : ArrayPool<int>.Shared.Rent(frozen.PendingBatchCount + 1);
        if (frozen.PendingBatchCount > 0)
        {
            batchStarts[0] = 0;
            for (var i = 0; i < frozen.PendingBatchCount; i++)
                batchStarts[i + 1] = batchStarts[i] + frozen.BatchCompCounts[i];
        }

        try
        {
            for (var i = 0; i < frozen.EntryCount; i++)
            {
                ref readonly var entry = ref frozen.Entries[i];

                switch (entry.Kind)
                {
                case CmdKind.Create:
                {
                    var count = frozen.BatchCompCounts[entry.DataIndex];
                    delta.ReservedEntities.Add(entry.Entity);
                    if (count > 0)
                    {
                        var comps = new RawComponentValue[count];
                        for (var c = 0; c < count; c++)
                        {
                            ref var bc = ref frozen.BatchComps[batchStarts[entry.DataIndex] + c];
                            comps[c] = frozen.Stores[bc.Type.Value]!.ReadRaw(bc.DataIndex, bc.Type);
                        }
                        delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, comps));
                    }
                    else
                    {
                        delta.ReleasedEntities.Add(entry.Entity);
                    }
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
        }
        finally
        {
            if (batchStarts.Length > 0)
                ArrayPool<int>.Shared.Return(batchStarts);
        }

        var copiedBytes = delta.DeepCopyOwnedData();
        return (delta, copiedBytes);
    }

    private void BuildDelta(FrameDelta delta)
    {
        var batchStarts = _pendingBatchCount == 0
            ? []
            : ArrayPool<int>.Shared.Rent(_pendingBatchCount + 1);
        if (_pendingBatchCount > 0)
        {
            batchStarts[0] = 0;
            for (var i = 0; i < _pendingBatchCount; i++)
                batchStarts[i + 1] = batchStarts[i] + _batchCompCounts[i];
        }

        try
        {
            for (var i = 0; i < _entryCount; i++)
            {
                ref readonly var entry = ref _entries[i];

                switch (entry.Kind)
                {
                    case CmdKind.Create:
                    {
                        var count = _batchCompCounts[entry.DataIndex];
                        delta.ReservedEntities.Add(entry.Entity);
                        if (count > 0)
                        {
                            var comps = new RawComponentValue[count];
                            for (var c = 0; c < count; c++)
                            {
                                ref var bc = ref _batchComps[batchStarts[entry.DataIndex] + c];
                                comps[c] = _stores[bc.Type.Value]!.ReadRaw(bc.DataIndex, bc.Type);
                            }
                            delta.CreatedEntities.Add(new RawCreatedEntity(entry.Entity, comps));
                        }
                        else
                        {
                            delta.ReleasedEntities.Add(entry.Entity);
                        }
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
        finally
        {
            if (batchStarts.Length > 0)
                ArrayPool<int>.Shared.Return(batchStarts);
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

    private ComponentStore GetOrCreateStore(ComponentType type)
    {
        var id = type.Value;
        if (id >= _stores.Length)
            Array.Resize(ref _stores, id + 1);

        var store = _stores[id];
        if (store == null)
        {
            var runtimeType = ComponentRegistry.Shared.GetType(type);
            var typedStoreType = typeof(ComponentStore<>).MakeGenericType(runtimeType);
            store = (ComponentStore)Activator.CreateInstance(typedStoreType)!;
            _stores[id] = store;
        }
        return store;
    }

    private void Clear()
    {
        _entryCount = 0;
        for (var i = 0; i < _stores.Length; i++)
            _stores[i]?.Clear();

        // Clear pending batch tracking
        for (var i = _pendingBatchMin; i < _pendingBatchMax && i < _pendingBatch.Length; i++)
            _pendingBatch[i] = -1;
        _pendingBatchCount = 0;
        _pendingBatchMin = int.MaxValue;
        _pendingBatchMax = 0;
        _batchCompTotal = 0;
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

    internal readonly record struct ComponentRef(
        ComponentType Type,
        ComponentStore Store,
        int DataIndex);

    internal abstract class ComponentStore
    {
        public abstract void WriteToWorld(World world, Entity entity, ComponentType type, int dataIndex, bool isAdd);
        public abstract void WriteToArchetype(Archetype archetype, int rowIndex, ComponentType type, int dataIndex);
        public abstract RawComponentValue ReadRaw(int dataIndex, ComponentType type);
        public abstract RawComponentCommand ReadRawCommand(Entity entity, ComponentType type, int dataIndex, int offset, int size);
        public abstract int AppendFromArchetype(Archetype archetype, int columnIndex, int rowIndex);
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

        public override void WriteToArchetype(Archetype archetype, int rowIndex, ComponentType type, int dataIndex)
        {
            archetype.SetComponentAtTyped(archetype.GetComponentIndex(type), rowIndex, in _data[dataIndex]);
        }

        public override RawComponentValue ReadRaw(int dataIndex, ComponentType type)
        {
            var size = Unsafe.SizeOf<T>();
            var bytes = new byte[size];
            Unsafe.WriteUnaligned(ref bytes[0], _data[dataIndex]);
            return new RawComponentValue(type, bytes, 0, size);
        }

        public override RawComponentCommand ReadRawCommand(Entity entity, ComponentType type, int dataIndex, int offset, int size)
        {
            var s = Unsafe.SizeOf<T>();
            var bytes = new byte[s];
            Unsafe.WriteUnaligned(ref bytes[0], _data[dataIndex]);
            return new RawComponentCommand(entity, type, offset, s, bytes);
        }

        public override int AppendFromArchetype(Archetype archetype, int columnIndex, int rowIndex)
        {
            var value = archetype.GetComponentAt<T>(columnIndex, rowIndex);
            return Append(value);
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
        public int PendingBatchMin;
        public int PendingBatchMax;
        public int[] BatchCompCounts = [];
        public BatchedComponent[] BatchComps = [];
        public int BatchCompTotal;
    }
}
