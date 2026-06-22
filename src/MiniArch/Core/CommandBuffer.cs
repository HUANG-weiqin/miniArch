using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands with per-entity deduplication.
/// Single-threaded: records directly into World without a compile pass.
/// </summary>
public sealed class CommandBuffer : ICommandRecorder
{
    private const int DefaultSlabSize = 4096;

    private readonly World _world;
    private InlineMap<int, EntityOpSlot>[] _opsPool = Array.Empty<InlineMap<int, EntityOpSlot>>();
    private Entity[] _opsEntityByPoolIndex = Array.Empty<Entity>();
    private int _opsPoolCount;
    private int[] _opsLookup = Array.Empty<int>();
    private int[] _opsTouchedIds = Array.Empty<int>();
    private int _opsTouchedIdCount;
    private int _maxOpsEntityId;
    private ulong[] _opsSeenMask = [];  // bitmask per entity: 8 ulongs (512 bits), flat array indexed by (opsIdx*8 + componentTypeId/64)
    private Entity[] _existingDestroyEntities = [];
    private int _existingDestroyCount;
    private bool _existingDestroySorted = true;
    private CreatedState[] _createdStatePool = Array.Empty<CreatedState>();
    private Entity[] _createdEntityByPoolIndex = Array.Empty<Entity>();
    private int _createdStatePoolCount;
    private int[] _createdStateLookup = Array.Empty<int>();
    private int[] _createdStateTouchedIds = Array.Empty<int>();
    private int _createdStateTouchedIdCount;
    private int _maxCreatedEntityId;
    private Dictionary<Entity, HierarchyIntent> _hierarchyByChild = new();
    private (ComponentType ComponentType, int Size)[] _typeInfoCache = [];
    private List<byte[]> _slabs = new();
    private readonly List<(int ComponentTypeId, CreatedComponent Component)> _tempComponents = new();
    private OverflowPool<int, EntityOpSlot> _opsOverflow;
    private OverflowPool<int, CreatedComponent> _createdOverflow;
    private bool _hasCreatedEntities;
    private int _currentSlabIndex = -1;
    private int _currentSlabOffset;

    [ThreadStatic] private static ComponentType[]? _tsExtractTypes;
    [ThreadStatic] private static CreatedComponent[]? _tsExtractSources;

    /// <summary>
    /// Creates a buffer for a world.
    /// </summary>
    public CommandBuffer(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }


    /// <summary>
    /// Records an entity creation.
    /// </summary>
    public Entity Create()
    {
        var entity = _world.ReserveDeferredEntity();
        RegisterInCreatedState(entity);
        return entity;
    }

    private void RegisterInCreatedState(Entity entity)
    {
        if (_createdStatePoolCount >= _createdStatePool.Length)
        {
            var newSize = _createdStatePool.Length == 0 ? 64 : _createdStatePool.Length * 2;
            var newPool = new CreatedState[newSize];
            var newEntities = new Entity[newSize];
            if (_createdStatePoolCount > 0)
            {
                Array.Copy(_createdStatePool, newPool, _createdStatePoolCount);
                Array.Copy(_createdEntityByPoolIndex, newEntities, _createdStatePoolCount);
            }
            _createdStatePool = newPool;
            _createdEntityByPoolIndex = newEntities;
        }

        var index = _createdStatePoolCount++;
        _createdStatePool[index] = default;
        _createdStatePool[index].Map.OverflowHead = -1;
        _createdEntityByPoolIndex[index] = entity;

        if (entity.Id >= _createdStateLookup.Length)
        {
            var newLen = _createdStateLookup.Length == 0 ? 64 : _createdStateLookup.Length;
            while (newLen <= entity.Id) newLen *= 2;
            var newLookup = new int[newLen];
            Array.Fill(newLookup, -1);
            if (_createdStateLookup.Length > 0)
                Array.Copy(_createdStateLookup, newLookup, _createdStateLookup.Length);
            _createdStateLookup = newLookup;
        }

        _createdStateLookup[entity.Id] = index;
        AddTouchedId(ref _createdStateTouchedIds, ref _createdStateTouchedIdCount, entity.Id);
        if (entity.Id >= _maxCreatedEntityId) _maxCreatedEntityId = entity.Id + 1;
        _hasCreatedEntities = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCreatedStateIndex(Entity entity)
    {
        var id = entity.Id;
        if ((uint)id >= (uint)_createdStateLookup.Length) return -1;
        var idx = _createdStateLookup[id];
        return idx >= 0 && _createdEntityByPoolIndex[idx].Version == entity.Version ? idx : -1;
    }

    /// <summary>
    /// Records an add command.
    /// </summary>
    public void Add<T>(Entity entity, T component) where T : unmanaged
    {
        var componentTypeId = CommandTypeInfo<T>.Id;
        CopyData(component, CommandTypeInfo<T>.Size, out var slabIndex, out var offset);

        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                ref var state = ref _createdStatePool[createdIdx];
                state.Map.Set(componentTypeId, new CreatedComponent(slabIndex, offset, CommandTypeInfo<T>.Size), ref _createdOverflow);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        var slot = new EntityOpSlot { Kind = OpKindAdd, ComponentType = CommandTypeInfo<T>.Type, SlabIndex = slabIndex, DataOffset = offset, DataSize = CommandTypeInfo<T>.Size };
        TryAddOpFast(opsIdx, componentTypeId, slot);
    }

    /// <summary>
    /// Records a set command.
    /// </summary>
    public void Set<T>(Entity entity, T component) where T : unmanaged
    {
        var componentTypeId = CommandTypeInfo<T>.Id;
        CopyData(component, CommandTypeInfo<T>.Size, out var slabIndex, out var offset);

        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                ref var state = ref _createdStatePool[createdIdx];
                state.Map.Set(componentTypeId, new CreatedComponent(slabIndex, offset, CommandTypeInfo<T>.Size), ref _createdOverflow);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        var slot = new EntityOpSlot { Kind = OpKindSet, ComponentType = CommandTypeInfo<T>.Type, SlabIndex = slabIndex, DataOffset = offset, DataSize = CommandTypeInfo<T>.Size };
        TryAddOpFast(opsIdx, componentTypeId, slot);
    }

    /// <summary>
    /// Records a remove command.
    /// </summary>
    public void Remove<T>(Entity entity) where T : unmanaged
    {
        var componentTypeId = CommandTypeInfo<T>.Id;

        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                ref var state = ref _createdStatePool[createdIdx];
                state.Map.Remove(componentTypeId, ref _createdOverflow);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        var slot = new EntityOpSlot { Kind = OpKindRemove, ComponentType = CommandTypeInfo<T>.Type };
        TryAddOpFast(opsIdx, componentTypeId, slot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryAddOpFast(int opsIdx, int componentTypeId, EntityOpSlot slot)
    {
        var wordIdx = componentTypeId >> 6;
        var bit = 1UL << (componentTypeId & 63);
        var maskBase = opsIdx * 8;
        ref var maskWord = ref _opsSeenMask[maskBase + wordIdx];
        ref var ops = ref _opsPool[opsIdx];
        if ((maskWord & bit) == 0)
        {
            maskWord |= bit;
            switch (ops.Count)
            {
                case 0: ops.Key0 = componentTypeId; ops.Value0 = slot; ops.Count = 1; return;
                case 1: ops.Key1 = componentTypeId; ops.Value1 = slot; ops.Count = 2; return;
                case 2: ops.Key2 = componentTypeId; ops.Value2 = slot; ops.Count = 3; return;
                case 3: ops.Key3 = componentTypeId; ops.Value3 = slot; ops.Count = 4; return;
                default:
                    ops.OverflowHead = _opsOverflow.Add(componentTypeId, slot, ops.OverflowCount > 0 ? ops.OverflowHead : -1);
                    ops.OverflowCount++;
                    return;
            }
        }
        ops.Set(componentTypeId, slot, ref _opsOverflow);
    }

    /// <summary>
    /// Records a destroy command.
    /// </summary>
    public void Destroy(Entity entity)
    {
        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                _createdStatePool[createdIdx].Destroyed = true;
                MarkCreatedDescendantsDestroyed(entity);
                return;
            }
        }

        AddExistingDestroy(entity);
    }

    /// <summary>
    /// Records a deep clone of an entity and its entire child subtree.
    /// Component data and child links are snapshotted at record time.
    /// Pending commands in this buffer and later world changes are not observed by the clone.
    /// Parent-child links within the subtree are preserved; the clone root does not inherit the source parent.
    /// </summary>
    /// <param name="source">The entity to clone. Must be alive at record time.</param>
    /// <returns>A deferred entity handle that becomes alive after <see cref="Submit"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="source"/> is no longer alive.</exception>
    public Entity Clone(Entity source)
    {
        if (!_world.TryGetLocation(source, out var location))
        {
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");
        }

        var deferred = _world.ReserveDeferredEntity();
        RegisterInCreatedState(deferred);
        SnapshotEntityToCreatedState(deferred, location);
        CloneChildrenAtRecordTime(source, deferred);
        return deferred;
    }

    private void CloneChildrenAtRecordTime(Entity sourceRoot, Entity cloneRoot)
    {
        if (!_world.Hierarchy.HasChildren(sourceRoot)) return;

        var stack = ArrayPool<CloneChildWork>.Shared.Rent(16);
        var stackCount = 0;
        try
        {
            PushCloneChildren(sourceRoot, cloneRoot, ref stack, ref stackCount);

            while (stackCount > 0)
            {
                var work = stack[--stackCount];
                var cloneParent = work.CloneParent;
                var srcChild = work.SourceChild;

                if (!_world.TryGetLocation(srcChild, out var childLocation)) continue;

                var cloneChild = _world.ReserveDeferredEntity();
                RegisterInCreatedState(cloneChild);
                SnapshotEntityToCreatedState(cloneChild, childLocation);
                _hierarchyByChild[cloneChild] = new HierarchyIntent(true, cloneParent);

                PushCloneChildren(srcChild, cloneChild, ref stack, ref stackCount);
            }
        }
        finally
        {
            ArrayPool<CloneChildWork>.Shared.Return(stack);
        }
    }

    private void PushCloneChildren(
        Entity sourceParent,
        Entity cloneParent,
        ref CloneChildWork[] stack,
        ref int stackCount)
    {
        foreach (var child in _world.Hierarchy.EnumerateChildren(_world, sourceParent))
        {
            PushPooled(ref stack, ref stackCount, new CloneChildWork(cloneParent, child));
        }
    }

    private void SnapshotEntityToCreatedState(Entity deferred, EntityInfo location)
    {
        var createdIdx = GetCreatedStateIndex(deferred);
        if (createdIdx < 0) return;

        var archetype = location.Archetype;
        var sourceRow = location.RowIndex;

        var components = archetype.Signature.AsSpan();
        for (var i = 0; i < components.Length; i++)
        {
            var componentType = components[i];
            var componentTypeId = componentType.Value;
            ref var state = ref _createdStatePool[createdIdx];
            var info = ResolveTypeInfo(componentTypeId);
            CopyComponentFromArchetype(archetype, i, sourceRow, info.Size, out var slabIndex, out var offset);
            state.Map.Set(componentTypeId, new CreatedComponent(slabIndex, offset, info.Size), ref _createdOverflow);
        }
    }

    private void MarkCreatedDescendantsDestroyed(Entity root)
    {
        if (_hierarchyByChild.Count == 0) return;

        Dictionary<Entity, List<Entity>> parentToChildren = new(_hierarchyByChild.Count);
        foreach (var (child, intent) in _hierarchyByChild)
        {
            if (!intent.IsLinked) continue;
            if (!parentToChildren.TryGetValue(intent.Parent, out var list))
            {
                list = new List<Entity>();
                parentToChildren[intent.Parent] = list;
            }
            list.Add(child);
        }

        var stack = ArrayPool<Entity>.Shared.Rent(16);
        var stackCount = 0;
        try
        {
            PushPooled(ref stack, ref stackCount, root);

            while (stackCount > 0)
            {
                var parent = stack[--stackCount];

                if (!parentToChildren.TryGetValue(parent, out var children)) continue;

                foreach (var child in children)
                {
                    var createdIdx = GetCreatedStateIndex(child);
                    if (createdIdx < 0) continue;

                    _createdStatePool[createdIdx].Destroyed = true;
                    PushPooled(ref stack, ref stackCount, child);
                }
            }
        }
        finally
        {
            ArrayPool<Entity>.Shared.Return(stack);
        }
    }

    private void AddExistingDestroy(Entity entity)
    {
        if (_existingDestroyCount == _existingDestroyEntities.Length)
        {
            var newCapacity = _existingDestroyEntities.Length == 0 ? 4 : _existingDestroyEntities.Length * 2;
            Array.Resize(ref _existingDestroyEntities, newCapacity);
        }
        _existingDestroyEntities[_existingDestroyCount] = entity;
        _existingDestroyCount++;
        _existingDestroySorted = false;
    }

    private int FindExistingDestroy(Entity entity)
    {
        return FindExistingDestroy(_existingDestroyEntities, _existingDestroyCount, entity);
    }

    private static int FindExistingDestroy(Entity[] entities, int count, Entity entity)
    {
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var comparison = entities[mid].CompareTo(entity);
            if (comparison == 0) return mid;
            if (comparison < 0) low = mid + 1;
            else high = mid - 1;
        }

        return ~low;
    }

    private void DeduplicateExistingDestroyEntities()
    {
        _existingDestroyCount = SortAndDeduplicateExistingDestroyEntities(
            _existingDestroyEntities,
            _existingDestroyCount,
            ref _existingDestroySorted);
    }

    private static int SortAndDeduplicateExistingDestroyEntities(Entity[] entities, int count, ref bool sorted)
    {
        if (count <= 1)
        {
            sorted = true;
            return count;
        }

        if (!sorted)
        {
            Array.Sort(entities, 0, count);
            sorted = true;
        }

        var write = 1;
        var previous = entities[0];
        for (var read = 1; read < count; read++)
        {
            var current = entities[read];
            if (previous.CompareTo(current) == 0)
            {
                continue;
            }

            entities[write++] = current;
            previous = current;
        }

        return write;
    }

    /// <summary>
    /// Records a parent link.
    /// </summary>
    public void Link(Entity parent, Entity child)
    {
        _hierarchyByChild[child] = new HierarchyIntent(true, parent);
    }

    /// <summary>
    /// Records a parent unlink.
    /// </summary>
    public void Unlink(Entity child)
    {
        _hierarchyByChild[child] = new HierarchyIntent(false, default);
    }

    /// <summary>
    /// Submits recorded commands directly to world without a compile pass.
    /// </summary>
    public bool Submit()
    {
        if (_opsPoolCount == 0 &&
            _existingDestroyCount == 0 && _createdStatePoolCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < _createdStatePoolCount; i++)
        {
            ref readonly var state = ref _createdStatePool[i];
            var entity = _createdEntityByPoolIndex[i];
            if (state.Destroyed)
            {
                _world.ReleaseReservedEntity(entity);
            }
            else if (state.Map.IsEmpty)
            {
                _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            }
            else
            {
                var (archetype, count, types) = BuildCreatedEntityComponents(in state, out var sources);
                _world.MaterializeReservedEntityFast(entity, archetype, types,
                    new ReadOnlySpan<CreatedComponent>(sources, 0, count),
                    _slabs);
            }
        }

        // Hierarchy before Ops — aligns with BuildDelta order (Create→Hierarchy→Ops→Destroy)
        // so Submit(source) and Replay(replica) converge for all command combinations.
        DeduplicateExistingDestroyEntities();

        foreach (var (child, intent) in _hierarchyByChild)
        {
            if (FindExistingDestroy(child) >= 0) continue;
            if (_hasCreatedEntities)
            {
                var csIdx = GetCreatedStateIndex(child);
                if (csIdx >= 0 && _createdStatePool[csIdx].Destroyed) continue;
                if (intent.IsLinked)
                {
                    var parentIdx = GetCreatedStateIndex(intent.Parent);
                    if (parentIdx >= 0 && _createdStatePool[parentIdx].Destroyed) continue;
                }
            }

            if (intent.IsLinked)
            {
                if (FindExistingDestroy(intent.Parent) >= 0) continue;
                _world.Link(intent.Parent, child);
            }
            else
                _world.Unlink(child);
        }

        for (var i = 0; i < _opsPoolCount; i++)
        {
            ref var existingOps = ref _opsPool[i];
            if (existingOps.IsEmpty) continue;
            var entity = _opsEntityByPoolIndex[i];

            if (existingOps.Count >= 1) ApplyOpDirect(existingOps.Value0, entity);
            if (existingOps.Count >= 2) ApplyOpDirect(existingOps.Value1, entity);
            if (existingOps.Count >= 3) ApplyOpDirect(existingOps.Value2, entity);
            if (existingOps.Count >= 4) ApplyOpDirect(existingOps.Value3, entity);
            for (var nodeIdx = existingOps.OverflowHead; nodeIdx >= 0; nodeIdx = _opsOverflow.GetNext(nodeIdx))
                ApplyOpDirect(_opsOverflow.GetValueReadonly(nodeIdx), entity);
        }

        for (var i = 0; i < _existingDestroyCount; i++)
        {
            var entity = _existingDestroyEntities[i];
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }

        Clear();
        return true;
    }

    /// <summary>
    /// Builds and returns a self-contained FrameDelta without replaying.
    /// The returned delta owns its own data and is independent of this buffer.
    /// </summary>
    public FrameDelta Snapshot()
    {
        var delta = new FrameDelta();
        BuildDelta(delta);
        return delta;
    }

    /// <summary>
    /// Swaps out internal state, submits to world on the calling thread,
    /// and builds a self-contained FrameDelta on a background thread.
    /// The returned delta owns its own data and is independent of this buffer.
    /// </summary>
    public Task<FrameDelta> SubmitAndSnapshotAsync()
    {
        if (_opsPoolCount == 0 &&
            _existingDestroyCount == 0 && _createdStatePoolCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return Task.FromResult(new FrameDelta());
        }

        var frozen = SwapOutState();
        // Static delegate + state parameter avoids the per-call closure allocation
        // that Task.Run(() => ...) would create. FrozenBufferState is a reference
        // type, so passing it as `object` is a free upcast — no boxing.
        var task = Task.Factory.StartNew(
            s_buildFromFrozen, frozen, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        SubmitFromFrozen(frozen);

        return task.ContinueWith(t =>
        {
            foreach (var slab in frozen.Slabs)
                ArrayPool<byte>.Shared.Return(slab);
            frozen.OpsOverflow.ReturnArrays();
            frozen.CreatedOverflow.ReturnArrays();
            return t.Result;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private static readonly Func<object?, FrameDelta> s_buildFromFrozen =
        state => BuildFromFrozen((FrozenBufferState)state!);

    private FrozenBufferState SwapOutState()
    {
        DeduplicateExistingDestroyEntities();

        var frozen = new FrozenBufferState
        {
            CreatedStatePool = _createdStatePool,
            CreatedStatePoolCount = _createdStatePoolCount,
            CreatedEntityByPoolIndex = _createdEntityByPoolIndex,
            CreatedStateLookup = _createdStateLookup,
            OpsPool = _opsPool,
            OpsPoolCount = _opsPoolCount,
            OpsEntityByPoolIndex = _opsEntityByPoolIndex,
            OpsLookup = _opsLookup,
            ExistingDestroyEntities = _existingDestroyEntities,
            ExistingDestroyCount = _existingDestroyCount,
            HierarchyByChild = _hierarchyByChild,
            Slabs = _slabs,
            HasCreatedEntities = _hasCreatedEntities,
            OpsOverflow = _opsOverflow,
            CreatedOverflow = _createdOverflow,
        };

        _createdStatePool = Array.Empty<CreatedState>();
        _createdStatePoolCount = 0;
        _createdEntityByPoolIndex = Array.Empty<Entity>();
        _createdStateLookup = Array.Empty<int>();
        _createdStateTouchedIds = Array.Empty<int>();
        _createdStateTouchedIdCount = 0;
        _maxCreatedEntityId = 0;
        _opsPool = Array.Empty<InlineMap<int, EntityOpSlot>>();
        _opsPoolCount = 0;
        _opsEntityByPoolIndex = Array.Empty<Entity>();
        _opsLookup = Array.Empty<int>();
        _opsTouchedIds = Array.Empty<int>();
        _opsTouchedIdCount = 0;
        _maxOpsEntityId = 0;
        _existingDestroyCount = 0;
        _existingDestroyEntities = [];
        _existingDestroySorted = true;
        _hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        _slabs = new List<byte[]>();
        _hasCreatedEntities = false;
        _typeInfoCache = [];
        _opsOverflow = default;
        _createdOverflow = default;
        _currentSlabIndex = -1;
        _currentSlabOffset = 0;

        return frozen;
    }

    private void SubmitFromFrozen(FrozenBufferState frozen)
    {
        for (var i = 0; i < frozen.CreatedStatePoolCount; i++)
        {
            ref readonly var state = ref frozen.CreatedStatePool[i];
            var entity = frozen.CreatedEntityByPoolIndex[i];
            if (state.Destroyed)
            {
                _world.ReleaseReservedEntity(entity);
            }
            else if (state.Map.IsEmpty)
            {
                _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
            }
            else
            {
                _tempComponents.Clear();
                state.Map.CopyTo(_tempComponents, ref frozen.CreatedOverflow);
                var componentCount = _tempComponents.Count;

                var components = ArrayPool<ComponentType>.Shared.Rent(componentCount);
                var sourceComponents = ArrayPool<CreatedComponent>.Shared.Rent(componentCount);
                var rawComponents = ArrayPool<RawComponentValue>.Shared.Rent(componentCount);
                try
                {
                    for (var j = 0; j < componentCount; j++)
                    {
                        sourceComponents[j] = _tempComponents[j].Component;
                        components[j] = (ComponentType)_tempComponents[j].ComponentTypeId;
                    }

                    Array.Sort(components, sourceComponents, 0, componentCount);

                    // Zero-allocation archetype lookup via order-independent set comparison
                    var archetype = _world.TryGetArchetype(components.AsSpan(0, componentCount));
                    if (archetype == null)
                    {
                        var comps = new ComponentType[componentCount];
                        Array.Copy(components, comps, componentCount);
                        archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));
                    }

                    for (var j = 0; j < componentCount; j++)
                    {
                        var sc = sourceComponents[j];
                        rawComponents[j] = new RawComponentValue(components[j], frozen.Slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
                    }

                    _world.MaterializeReservedEntityDirect(entity, archetype, rawComponents.AsSpan(0, componentCount));
                }
                finally
                {
                    ArrayPool<RawComponentValue>.Shared.Return(rawComponents);
                    ArrayPool<CreatedComponent>.Shared.Return(sourceComponents);
                    ArrayPool<ComponentType>.Shared.Return(components);
                }
            }
        }

        // Hierarchy before Ops — aligns with BuildDelta order (Create→Hierarchy→Ops→Destroy)
        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, child) >= 0) continue;
            if (frozen.HasCreatedEntities)
            {
                if (IsFrozenCreatedDestroyed(frozen, child)) continue;
                if (intent.IsLinked && IsFrozenCreatedDestroyed(frozen, intent.Parent)) continue;
            }

            if (intent.IsLinked)
            {
                if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, intent.Parent) >= 0) continue;
                _world.Link(intent.Parent, child);
            }
            else
                _world.Unlink(child);
        }

        for (var i = 0; i < frozen.OpsPoolCount; i++)
        {
            ref var existingOps = ref frozen.OpsPool[i];
            if (existingOps.IsEmpty) continue;
            var entity = frozen.OpsEntityByPoolIndex[i];

            if (existingOps.Count >= 1) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value0, entity);
            if (existingOps.Count >= 2) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value1, entity);
            if (existingOps.Count >= 3) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value2, entity);
            if (existingOps.Count >= 4) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value3, entity);
            for (var nodeIdx = existingOps.OverflowHead; nodeIdx >= 0; nodeIdx = frozen.OpsOverflow.GetNext(nodeIdx))
                ApplyOpDirectFromFrozen(_world, frozen.Slabs, frozen.OpsOverflow.GetValueReadonly(nodeIdx), entity);
        }

        for (var i = 0; i < frozen.ExistingDestroyCount; i++)
        {
            var entity = frozen.ExistingDestroyEntities[i];
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }
    }

    private static FrameDelta BuildFromFrozen(FrozenBufferState frozen)
    {
        var delta = new FrameDelta();

        // Pass 1: All reserves
        for (var poolIdx = 0; poolIdx < frozen.CreatedStatePoolCount; poolIdx++)
        {
            delta.AddReserve(frozen.CreatedEntityByPoolIndex[poolIdx]);
        }

        // Pass 2: All releases for destroyed entities
        for (var poolIdx = 0; poolIdx < frozen.CreatedStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref frozen.CreatedStatePool[poolIdx];
            if (state.Destroyed)
                delta.AddRelease(frozen.CreatedEntityByPoolIndex[poolIdx]);
        }

        // Pass 3: All creates for surviving entities
        for (var poolIdx = 0; poolIdx < frozen.CreatedStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref frozen.CreatedStatePool[poolIdx];
            if (!state.Destroyed)
            {
                var entity = frozen.CreatedEntityByPoolIndex[poolIdx];
                if (state.Map.IsEmpty)
                    delta.AddCreate(entity, Array.Empty<RawComponentValue>());
                else
                    delta.AddCreate(entity, BuildCreatedEntityComponentsFromFrozen(in state, frozen.Slabs, ref frozen.CreatedOverflow));
            }
        }

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, child) >= 0) continue;
            if (frozen.HasCreatedEntities)
            {
                if (IsFrozenCreatedDestroyed(frozen, child)) continue;
                if (intent.IsLinked && IsFrozenCreatedDestroyed(frozen, intent.Parent)) continue;
            }

            if (intent.IsLinked)
            {
                if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, intent.Parent) >= 0) continue;
                delta.AddLink(intent.Parent, child);
            }
            else
                delta.AddUnlink(child);
        }

        for (var i = 0; i < frozen.OpsPoolCount; i++)
        {
            ref var existingOps = ref frozen.OpsPool[i];
            if (existingOps.IsEmpty) continue;
            var entity = frozen.OpsEntityByPoolIndex[i];

            if (existingOps.Count >= 1) EmitOpFromFrozen(frozen.Slabs, in existingOps.Value0, entity, delta);
            if (existingOps.Count >= 2) EmitOpFromFrozen(frozen.Slabs, in existingOps.Value1, entity, delta);
            if (existingOps.Count >= 3) EmitOpFromFrozen(frozen.Slabs, in existingOps.Value2, entity, delta);
            if (existingOps.Count >= 4) EmitOpFromFrozen(frozen.Slabs, in existingOps.Value3, entity, delta);
            for (var nodeIdx = existingOps.OverflowHead; nodeIdx >= 0; nodeIdx = frozen.OpsOverflow.GetNext(nodeIdx))
            {
                var overflowSlot = frozen.OpsOverflow.GetValueReadonly(nodeIdx);
                EmitOpFromFrozen(frozen.Slabs, in overflowSlot, entity, delta);
            }
        }

        for (var i = 0; i < frozen.ExistingDestroyCount; i++)
            delta.AddDestroy(frozen.ExistingDestroyEntities[i]);

        return delta;
    }

    private static (ComponentType[] Types, CreatedComponent[] Sources, int Count) ExtractAndSortComponents(
        in CreatedState state, ref OverflowPool<int, CreatedComponent> pool)
    {
        var count = state.Map.Count + state.Map.OverflowCount;

        var types = ArrayPool<ComponentType>.Shared.Rent(count);
        var sources = ArrayPool<CreatedComponent>.Shared.Rent(count);

        var idx = 0;
        if (state.Map.Count >= 1) { sources[idx] = state.Map.Value0; types[idx] = (ComponentType)state.Map.Key0; idx++; }
        if (state.Map.Count >= 2) { sources[idx] = state.Map.Value1; types[idx] = (ComponentType)state.Map.Key1; idx++; }
        if (state.Map.Count >= 3) { sources[idx] = state.Map.Value2; types[idx] = (ComponentType)state.Map.Key2; idx++; }
        if (state.Map.Count >= 4) { sources[idx] = state.Map.Value3; types[idx] = (ComponentType)state.Map.Key3; idx++; }
        for (var nodeIdx = state.Map.OverflowHead; nodeIdx >= 0; nodeIdx = pool.GetNext(nodeIdx))
        {
            sources[idx] = pool.GetValueReadonly(nodeIdx);
            types[idx] = (ComponentType)pool.GetKeyReadonly(nodeIdx);
            idx++;
        }

        Array.Sort(types, sources, 0, idx);
        return (types, sources, idx);
    }

    private static bool IsFrozenCreatedDestroyed(FrozenBufferState frozen, Entity entity)
    {
        var id = entity.Id;
        if ((uint)id >= (uint)frozen.CreatedStateLookup.Length) return false;

        var csIdx = frozen.CreatedStateLookup[id];
        return csIdx >= 0
            && frozen.CreatedEntityByPoolIndex[csIdx].Version == entity.Version
            && frozen.CreatedStatePool[csIdx].Destroyed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PushPooled<T>(ref T[] array, ref int count, T value)
    {
        if ((uint)count >= (uint)array.Length)
        {
            GrowPooled(ref array);
        }

        array[count++] = value;
    }

    private static void GrowPooled<T>(ref T[] array)
    {
        var next = ArrayPool<T>.Shared.Rent(array.Length * 2);
        Array.Copy(array, next, array.Length);
        ArrayPool<T>.Shared.Return(array);
        array = next;
    }

    private static void ReturnExtracted(ComponentType[] types, CreatedComponent[] sources)
    {
        ArrayPool<CreatedComponent>.Shared.Return(sources);
        ArrayPool<ComponentType>.Shared.Return(types);
    }

    private static RawComponentValue[] BuildCreatedEntityComponentsFromFrozen(
        in CreatedState state, List<byte[]> slabs, ref OverflowPool<int, CreatedComponent> pool)
    {
        var (types, sources, count) = ExtractAndSortComponents(in state, ref pool);
        try
        {
            var rawComponents = new RawComponentValue[count];
            for (var i = 0; i < count; i++)
            {
                var sc = sources[i];
                rawComponents[i] = new RawComponentValue(types[i], slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
            }
            return rawComponents;
        }
        finally
        {
            ReturnExtracted(types, sources);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyOpDirectFromFrozen(World world, List<byte[]> slabs, EntityOpSlot slot, Entity entity)
    {
        if (!world.TryGetLocation(entity, out var loc)) return;
        var record = new EntityRecord { Archetype = loc.Archetype, RowIndex = loc.RowIndex, Version = loc.Version };
        switch (slot.Kind)
        {
            case OpKindAdd:
            case OpKindSet:
                unsafe
                {
                    fixed (byte* ptr = slabs[slot.SlabIndex])
                    {
                        world.ApplyRawAddOrSet(entity, record, slot.ComponentType, ptr + slot.DataOffset);
                    }
                }
                break;
            case OpKindRemove:
                world.RemoveBoxed(entity, record, slot.ComponentType);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitOpFromFrozen(
        List<byte[]> slabs,
        in EntityOpSlot slot, Entity entity, FrameDelta delta)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
                delta.AddAdd(entity, slot.ComponentType, slabs[slot.SlabIndex], slot.DataOffset, slot.DataSize);
                break;
            case OpKindSet:
                delta.AddSet(entity, slot.ComponentType, slabs[slot.SlabIndex], slot.DataOffset, slot.DataSize);
                break;
            case OpKindRemove:
                delta.AddRemove(entity, slot.ComponentType);
                break;
        }
    }

    private (Archetype Archetype, int Count, ComponentType[] Types) BuildCreatedEntityComponents(in CreatedState state, out CreatedComponent[] sources)
    {
        var count = state.Map.Count + state.Map.OverflowCount;

        // ThreadStatic buffers — guaranteed non-null after the null-check block below
        var types = _tsExtractTypes;
        sources = _tsExtractSources!;
        if (types == null || types.Length < count)
        {
            types = new ComponentType[Math.Max(count, 16)];
            sources = new CreatedComponent[Math.Max(count, 16)];
            _tsExtractTypes = types;
            _tsExtractSources = sources;
        }

        var idx = 0;
        if (state.Map.Count >= 1) { sources![idx] = state.Map.Value0; types![idx] = (ComponentType)state.Map.Key0; idx++; }
        if (state.Map.Count >= 2) { sources[idx] = state.Map.Value1; types[idx] = (ComponentType)state.Map.Key1; idx++; }
        if (state.Map.Count >= 3) { sources[idx] = state.Map.Value2; types[idx] = (ComponentType)state.Map.Key2; idx++; }
        if (state.Map.Count >= 4) { sources[idx] = state.Map.Value3; types[idx] = (ComponentType)state.Map.Key3; idx++; }
        for (var nodeIdx = state.Map.OverflowHead; nodeIdx >= 0; nodeIdx = _createdOverflow.GetNext(nodeIdx))
        {
            sources![idx] = _createdOverflow.GetValueReadonly(nodeIdx);
            types![idx] = (ComponentType)_createdOverflow.GetKeyReadonly(nodeIdx);
            idx++;
        }

        Array.Sort(types, sources, 0, idx);

        // Zero-allocation archetype lookup via order-independent set comparison
        var archetype = _world.TryGetArchetype(types.AsSpan(0, idx));
        if (archetype == null)
        {
            var comps = new ComponentType[idx];
            Array.Copy(types, comps, idx);
            archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));
        }

        return (archetype, idx, types!);
    }

    private RawComponentValue[] BuildCreatedEntityComponentsForDelta(in CreatedState state)
    {
        var (types, sources, count) = ExtractAndSortComponents(in state, ref _createdOverflow);
        try
        {
            var rawComponents = new RawComponentValue[count];
            for (var i = 0; i < count; i++)
            {
                var sc = sources[i];
                rawComponents[i] = new RawComponentValue(types[i], _slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
            }
            return rawComponents;
        }
        finally
        {
            ReturnExtracted(types, sources);
        }
    }

    private void BuildDelta(FrameDelta delta)
    {
        DeduplicateExistingDestroyEntities();

        // Pass 1: All reserves (grouped so allocator state stays consistent with old sectioned ReplayCore)
        for (var poolIdx = 0; poolIdx < _createdStatePoolCount; poolIdx++)
        {
            var entity = _createdEntityByPoolIndex[poolIdx];
            delta.AddReserve(entity);
        }

        // Pass 2: All releases for destroyed entities
        for (var poolIdx = 0; poolIdx < _createdStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref _createdStatePool[poolIdx];
            if (state.Destroyed)
            {
                var entity = _createdEntityByPoolIndex[poolIdx];
                delta.AddRelease(entity);
            }
        }

        // Pass 3: All creates for surviving entities
        for (var poolIdx = 0; poolIdx < _createdStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref _createdStatePool[poolIdx];
            if (!state.Destroyed)
            {
                var entity = _createdEntityByPoolIndex[poolIdx];
                if (state.Map.IsEmpty)
                    delta.AddCreate(entity, Array.Empty<RawComponentValue>());
                else
                    delta.AddCreate(entity, BuildCreatedEntityComponentsForDelta(in state));
            }
        }

        foreach (var (child, intent) in _hierarchyByChild)
        {
            if (FindExistingDestroy(child) >= 0)
            {
                continue;
            }

            if (_hasCreatedEntities)
            {
                var csIdx = GetCreatedStateIndex(child);
                if (csIdx >= 0 && _createdStatePool[csIdx].Destroyed)
                {
                    continue;
                }

                if (intent.IsLinked)
                {
                    var parentIdx = GetCreatedStateIndex(intent.Parent);
                    if (parentIdx >= 0 && _createdStatePool[parentIdx].Destroyed)
                    {
                        continue;
                    }
                }
            }

            if (intent.IsLinked)
            {
                if (FindExistingDestroy(intent.Parent) >= 0) continue;
                delta.AddLink(intent.Parent, child);
            }
            else
            {
                delta.AddUnlink(child);
            }
        }

        for (var i = 0; i < _opsPoolCount; i++)
        {
            ref var existingOps = ref _opsPool[i];
            if (existingOps.IsEmpty) continue;
            var entity = _opsEntityByPoolIndex[i];

            if (existingOps.Count >= 1) EmitOp(in existingOps.Value0, entity, delta);
            if (existingOps.Count >= 2) EmitOp(in existingOps.Value1, entity, delta);
            if (existingOps.Count >= 3) EmitOp(in existingOps.Value2, entity, delta);
            if (existingOps.Count >= 4) EmitOp(in existingOps.Value3, entity, delta);
            for (var nodeIdx = existingOps.OverflowHead; nodeIdx >= 0; nodeIdx = _opsOverflow.GetNext(nodeIdx))
            {
                var overflowSlot = _opsOverflow.GetValueReadonly(nodeIdx);
                EmitOp(in overflowSlot, entity, delta);
            }
        }

        for (var i = 0; i < _existingDestroyCount; i++)
        {
            delta.AddDestroy(_existingDestroyEntities[i]);
        }
    }

    private void Clear()
    {
        _existingDestroyCount = 0;
        for (int i = 0; i < _opsPoolCount; i++)
        {
            _opsPool[i].Clear();
        }
        for (int i = 0; i < _opsTouchedIdCount; i++)
        {
            _opsLookup[_opsTouchedIds[i]] = -1;
        }
        _opsPoolCount = 0;
        _opsTouchedIdCount = 0;
        _maxOpsEntityId = 0;
        for (int i = 0; i < _createdStatePoolCount; i++)
        {
            _createdStatePool[i].Map.Clear();
        }

        for (int i = 0; i < _createdStateTouchedIdCount; i++)
        {
            _createdStateLookup[_createdStateTouchedIds[i]] = -1;
        }

        _createdStatePoolCount = 0;
        _createdStateTouchedIdCount = 0;
        _maxCreatedEntityId = 0;
        _hierarchyByChild.Clear();
        _hasCreatedEntities = false;
        _opsOverflow.Clear();
        _createdOverflow.Clear();
        _existingDestroySorted = true;

        _currentSlabIndex = _slabs.Count == 0 ? -1 : 0;
        _currentSlabOffset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSlabSpace(int size)
    {
        if (_currentSlabIndex < 0 || _currentSlabOffset + size > _slabs[_currentSlabIndex].Length)
        {
            var nextIndex = _currentSlabIndex + 1;
            if ((uint)nextIndex < (uint)_slabs.Count && _slabs[nextIndex].Length >= size)
            {
                _currentSlabIndex = nextIndex;
            }
            else
            {
                var slabSize = size > DefaultSlabSize ? size : DefaultSlabSize;
                var newSlab = ArrayPool<byte>.Shared.Rent(slabSize);
                _slabs.Add(newSlab);
                _currentSlabIndex = _slabs.Count - 1;
            }
            _currentSlabOffset = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyData<T>(T component, int size, out int slabIndex, out int offset) where T : unmanaged
    {
        EnsureSlabSpace(size);

        slabIndex = _currentSlabIndex;
        offset = _currentSlabOffset;
        _currentSlabOffset += size;

        fixed (byte* ptr = &_slabs[slabIndex][offset])
        {
            Unsafe.Write(ptr, component);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyComponentFromArchetype(Archetype archetype, int columnIndex, int row, int size, out int slabIndex, out int offset)
    {
        EnsureSlabSpace(size);

        slabIndex = _currentSlabIndex;
        offset = _currentSlabOffset;
        _currentSlabOffset += size;

        fixed (byte* ptr = &_slabs[slabIndex][offset])
        {
            archetype.ReadComponentRaw(columnIndex, row, ptr);
        }
    }

    private (ComponentType ComponentType, int Size) ResolveTypeInfo(int componentTypeId)
    {
        if ((uint)componentTypeId < (uint)_typeInfoCache.Length)
        {
            var cached = _typeInfoCache[componentTypeId];
            if (cached.Size > 0)
            {
                return cached;
            }
        }

        var componentType = (ComponentType)componentTypeId;
        if (!ComponentRegistry.Shared.TryGetType(componentType, out var runtimeType))
        {
            componentType = default;
        }

        var size = runtimeType == null ? 0 : ComponentSizeCache.GetSize(runtimeType);
        var info = (componentType, size);

        if (componentTypeId >= _typeInfoCache.Length)
        {
            var newCache = new (ComponentType ComponentType, int Size)[componentTypeId + 1];
            if (_typeInfoCache.Length > 0)
                Array.Copy(_typeInfoCache, newCache, _typeInfoCache.Length);
            _typeInfoCache = newCache;
        }

        _typeInfoCache[componentTypeId] = info;
        return info;
    }

    private static class CommandTypeInfo<T> where T : unmanaged
    {
        public static readonly ComponentType Type = Component<T>.ComponentType;
        public static readonly int Id = Type.Value;
        public static readonly int Size = Unsafe.SizeOf<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddTouchedId(ref int[] ids, ref int count, int id)
    {
        if ((uint)count >= (uint)ids.Length)
        {
            Array.Resize(ref ids, ids.Length == 0 ? 64 : ids.Length * 2);
        }

        ids[count++] = id;
    }

    private const byte OpKindAdd = 1;
    private const byte OpKindSet = 2;
    private const byte OpKindRemove = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitOp(in EntityOpSlot slot, Entity entity, FrameDelta delta)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
                delta.AddAdd(entity, slot.ComponentType, _slabs[slot.SlabIndex], slot.DataOffset, slot.DataSize);
                break;
            case OpKindSet:
                delta.AddSet(entity, slot.ComponentType, _slabs[slot.SlabIndex], slot.DataOffset, slot.DataSize);
                break;
            case OpKindRemove:
                delta.AddRemove(entity, slot.ComponentType);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyOpDirect(EntityOpSlot slot, Entity entity)
    {
        if (!_world.TryGetLocation(entity, out var loc)) return;
        var record = new EntityRecord { Archetype = loc.Archetype, RowIndex = loc.RowIndex, Version = loc.Version };
        switch (slot.Kind)
        {
            case OpKindAdd:
            case OpKindSet:
                unsafe
                {
                    fixed (byte* ptr = _slabs[slot.SlabIndex])
                    {
                        _world.ApplyRawAddOrSet(entity, record, slot.ComponentType, ptr + slot.DataOffset);
                    }
                }
                break;
            case OpKindRemove:
                _world.RemoveBoxed(entity, record, slot.ComponentType);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetOrCreateOpsIndex(Entity entity)
    {
        var id = entity.Id;
        if ((uint)id < (uint)_opsLookup.Length)
        {
            var idx = _opsLookup[id];
            if (idx >= 0 && _opsEntityByPoolIndex[idx].Version == entity.Version)
                return idx;
        }
        return AllocateOpsIndex(entity);
    }

    private int AllocateOpsIndex(Entity entity)
    {
        var id = entity.Id;

        if (id >= _opsLookup.Length)
        {
            var newLen = _opsLookup.Length == 0 ? 64 : _opsLookup.Length;
            while (newLen <= id) newLen *= 2;
            var newLookup = new int[newLen];
            Array.Fill(newLookup, -1);
            if (_opsLookup.Length > 0)
                Array.Copy(_opsLookup, newLookup, _opsLookup.Length);
            _opsLookup = newLookup;
        }

        if (_opsPoolCount >= _opsPool.Length)
        {
            var newSize = _opsPool.Length == 0 ? 64 : _opsPool.Length * 2;
            var newPool = new InlineMap<int, EntityOpSlot>[newSize];
            var newEntities = new Entity[newSize];
            var newMask = new ulong[newSize * 8];
            if (_opsPoolCount > 0)
            {
                Array.Copy(_opsPool, newPool, _opsPoolCount);
                Array.Copy(_opsEntityByPoolIndex, newEntities, _opsPoolCount);
                Array.Copy(_opsSeenMask, newMask, _opsPoolCount * 8);
            }
            _opsPool = newPool;
            _opsEntityByPoolIndex = newEntities;
            _opsSeenMask = newMask;
        }

        var index = _opsPoolCount++;
        _opsPool[index].OverflowHead = -1;
        _opsEntityByPoolIndex[index] = entity;
        Array.Clear(_opsSeenMask, index * 8, 8);
        _opsLookup[id] = index;
        AddTouchedId(ref _opsTouchedIds, ref _opsTouchedIdCount, id);
        if (id >= _maxOpsEntityId) _maxOpsEntityId = id + 1;
        return index;
    }

    private struct EntityOpSlot
    {
        public byte Kind;
        public ComponentType ComponentType;
        public int SlabIndex;
        public int DataOffset;
        public int DataSize;
    }

    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);

    private readonly record struct CloneChildWork(Entity CloneParent, Entity SourceChild);

    private struct CreatedState
    {
        public InlineMap<int, CreatedComponent> Map;
        public bool Destroyed;
    }

    internal readonly record struct CreatedComponent(int SlabIndex, int DataOffset, int DataSize);

    private sealed class FrozenBufferState
    {
        public CreatedState[] CreatedStatePool = null!;
        public int CreatedStatePoolCount;
        public Entity[] CreatedEntityByPoolIndex = null!;
        public int[] CreatedStateLookup = null!;

        public InlineMap<int, EntityOpSlot>[] OpsPool = null!;
        public int OpsPoolCount;
        public Entity[] OpsEntityByPoolIndex = null!;
        public int[] OpsLookup = null!;

        public Entity[] ExistingDestroyEntities = null!;
        public int ExistingDestroyCount;
        public Dictionary<Entity, HierarchyIntent> HierarchyByChild = null!;
        public List<byte[]> Slabs = null!;
        public bool HasCreatedEntities;
        public OverflowPool<int, EntityOpSlot> OpsOverflow;
        public OverflowPool<int, CreatedComponent> CreatedOverflow;
    }
}
