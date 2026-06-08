using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands with per-entity deduplication.
/// Single-threaded: records directly into World without a compile pass.
/// </summary>
public sealed class CommandBuffer
{
    private const int DefaultSlabSize = 4096;

    private readonly World _world;
    private InlineMap<int, EntityOpSlot>[] _opsPool = Array.Empty<InlineMap<int, EntityOpSlot>>();
    private Entity[] _opsEntityByPoolIndex = Array.Empty<Entity>();
    private int _opsPoolCount;
    private int[] _opsLookup = Array.Empty<int>();
    private int _maxOpsEntityId;
    private Entity[] _existingDestroyEntities = [];
    private int _existingDestroyCount;
    private CreatedState[] _createdStatePool = Array.Empty<CreatedState>();
    private Entity[] _createdEntityByPoolIndex = Array.Empty<Entity>();
    private int _createdStatePoolCount;
    private int[] _createdStateLookup = Array.Empty<int>();
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

    // Bounded multi-entry archetype cache: avoids repeated Dictionary lookups and
    // temporary array/Signature allocations for the same component set.
    // 8 slots cover the attack pipeline's 6+ alternating archetypes with room to spare.
    private const int ArchetypeCacheSize = 4;
    private int _archetypeCacheCount;
    private int _archetypeCacheGeneration = -1;
    private readonly struct ArchetypeCacheEntry(int hash, int componentCount, Archetype archetype)
    {
        public readonly int Hash = hash;
        public readonly int ComponentCount = componentCount;
        public readonly Archetype Archetype = archetype;
    }
    private ArchetypeCacheEntry[] _archetypeCache = [];


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
        if (entity.Id >= _maxCreatedEntityId) _maxCreatedEntityId = entity.Id + 1;
        _hasCreatedEntities = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCreatedStateIndex(Entity entity)
    {
        var id = entity.Id;
        return (uint)id < (uint)_createdStateLookup.Length ? _createdStateLookup[id] : -1;
    }

    /// <summary>
    /// Records an add command.
    /// </summary>
    public void Add<T>(Entity entity, T component)
    {
        var componentTypeId = GetComponentTypeId<T>();
        var info = ResolveTypeInfo(componentTypeId);
        CopyData(component, info.Size, out var slabIndex, out var offset);

        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                ref var state = ref _createdStatePool[createdIdx];
                state.Map.Set(componentTypeId, new CreatedComponent(slabIndex, offset, info.Size), ref _createdOverflow);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        var slot = new EntityOpSlot { Kind = OpKindAdd, ComponentType = info.ComponentType, SlabIndex = slabIndex, DataOffset = offset, DataSize = info.Size };
        ops.Set(componentTypeId, slot, ref _opsOverflow);
    }

    /// <summary>
    /// Records a set command.
    /// </summary>
    public void Set<T>(Entity entity, T component)
    {
        var componentTypeId = GetComponentTypeId<T>();
        var info = ResolveTypeInfo(componentTypeId);
        CopyData(component, info.Size, out var slabIndex, out var offset);

        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                ref var state = ref _createdStatePool[createdIdx];
                state.Map.Set(componentTypeId, new CreatedComponent(slabIndex, offset, info.Size), ref _createdOverflow);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        var slot = new EntityOpSlot { Kind = OpKindSet, ComponentType = info.ComponentType, SlabIndex = slabIndex, DataOffset = offset, DataSize = info.Size };
        ops.Set(componentTypeId, slot, ref _opsOverflow);
    }

    /// <summary>
    /// Records a remove command.
    /// </summary>
    public void Remove<T>(Entity entity)
    {
        var componentTypeId = GetComponentTypeId<T>();

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
        ref var ops = ref _opsPool[opsIdx];
        var info = ResolveTypeInfo(componentTypeId);
        var slot = new EntityOpSlot { Kind = OpKindRemove, ComponentType = info.ComponentType };
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
        var index = FindExistingDestroy(entity);
        if (index >= 0) return;
        var insertIndex = ~index;
        if (_existingDestroyCount == _existingDestroyEntities.Length)
        {
            var newCapacity = _existingDestroyEntities.Length == 0 ? 4 : _existingDestroyEntities.Length * 2;
            Array.Resize(ref _existingDestroyEntities, newCapacity);
        }
        if (insertIndex < _existingDestroyCount)
            Array.Copy(_existingDestroyEntities, insertIndex, _existingDestroyEntities, insertIndex + 1, _existingDestroyCount - insertIndex);
        _existingDestroyEntities[insertIndex] = entity;
        _existingDestroyCount++;
    }

    private int FindExistingDestroy(Entity entity)
    {
        var low = 0;
        var high = _existingDestroyCount - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var comparison = CompareEntity(_existingDestroyEntities[mid], entity);
            if (comparison == 0) return mid;
            if (comparison < 0) low = mid + 1;
            else high = mid - 1;
        }

        return ~low;
    }

    private static int FindExistingDestroy(Entity[] entities, int count, Entity entity)
    {
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var comparison = CompareEntity(entities[mid], entity);
            if (comparison == 0) return mid;
            if (comparison < 0) low = mid + 1;
            else high = mid - 1;
        }

        return ~low;
    }

    private static int CompareEntity(Entity left, Entity right)
    {
        var idComparison = left.Id.CompareTo(right.Id);
        return idComparison != 0 ? idComparison : left.Version.CompareTo(right.Version);
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
                _world.Link(intent.Parent, child);
            else
                _world.Unlink(child);
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
        var copiedBytes = delta.DeepCopyOwnedData();
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
        var task = Task.Run(() => BuildFromFrozen(frozen));

        SubmitFromFrozen(frozen);

        return task.ContinueWith(t =>
        {
            foreach (var slab in frozen.Slabs)
                ArrayPool<byte>.Shared.Return(slab);
            frozen.OpsOverflow.ReturnArrays();
            frozen.CreatedOverflow.ReturnArrays();
            return t.Result.Delta;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private FrozenBufferState SwapOutState()
    {
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
        _maxCreatedEntityId = 0;
        _opsPool = Array.Empty<InlineMap<int, EntityOpSlot>>();
        _opsPoolCount = 0;
        _opsEntityByPoolIndex = Array.Empty<Entity>();
        _opsLookup = Array.Empty<int>();
        _maxOpsEntityId = 0;
        _existingDestroyCount = 0;
        _existingDestroyEntities = [];
        _hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        _slabs = new List<byte[]>();
        _hasCreatedEntities = false;
        _typeInfoCache = [];
        _opsOverflow = default;
        _createdOverflow = default;
        _currentSlabIndex = -1;
        _currentSlabOffset = 0;
        _archetypeCacheCount = 0;

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

                    // Use multi-entry archetype cache to avoid allocating ComponentType[] + Signature
                    var archetype = LookupArchetypeCache(components, componentCount);
                    if (archetype == null)
                    {
                        var comps = new ComponentType[componentCount];
                        Array.Copy(components, comps, componentCount);
                        archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));
                        InsertArchetypeCache(components, componentCount, archetype);
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

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, child) >= 0) continue;
            if (frozen.HasCreatedEntities)
            {
                if (IsFrozenCreatedDestroyed(frozen, child)) continue;
                if (intent.IsLinked && IsFrozenCreatedDestroyed(frozen, intent.Parent)) continue;
            }

            if (intent.IsLinked)
                _world.Link(intent.Parent, child);
            else
                _world.Unlink(child);
        }

        for (var i = 0; i < frozen.ExistingDestroyCount; i++)
        {
            var entity = frozen.ExistingDestroyEntities[i];
            if (_world.IsAlive(entity))
                _world.Destroy(entity);
        }
    }

    private static (FrameDelta Delta, int CopiedBytes) BuildFromFrozen(FrozenBufferState frozen)
    {
        var delta = new FrameDelta();

        var releasedCount = 0;
        var createdCount = 0;
        for (var i = 0; i < frozen.CreatedStatePoolCount; i++)
        {
            ref readonly var s = ref frozen.CreatedStatePool[i];
            if (s.Destroyed) releasedCount++;
            else createdCount++;
        }

        delta.ReservedEntities.EnsureCapacity(frozen.CreatedStatePoolCount);
        delta.ReleasedEntities.EnsureCapacity(releasedCount);
        delta.CreatedEntities.EnsureCapacity(createdCount);
        delta.LinkCommands.EnsureCapacity(frozen.HierarchyByChild.Count);
        delta.UnlinkCommands.EnsureCapacity(frozen.HierarchyByChild.Count);
        delta.AddCommands.EnsureCapacity(frozen.OpsPoolCount);
        delta.SetCommands.EnsureCapacity(frozen.OpsPoolCount);
        delta.RemoveCommands.EnsureCapacity(frozen.OpsPoolCount);
        delta.DestroyedEntities.EnsureCapacity(frozen.ExistingDestroyCount);

        for (var poolIdx = 0; poolIdx < frozen.CreatedStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref frozen.CreatedStatePool[poolIdx];
            var entity = frozen.CreatedEntityByPoolIndex[poolIdx];
            delta.ReservedEntities.Add(entity);
            if (state.Destroyed)
            {
                delta.ReleasedEntities.Add(entity);
            }
            else if (state.Map.IsEmpty)
            {
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, Array.Empty<RawComponentValue>()));
            }
            else
            {
                var components = BuildCreatedEntityComponentsFromFrozen(in state, frozen.Slabs, ref frozen.CreatedOverflow);
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, components));
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
                delta.LinkCommands.Add(new LinkCommand(intent.Parent, child));
            else
                delta.UnlinkCommands.Add(new UnlinkCommand(child));
        }

        for (var i = 0; i < frozen.OpsPoolCount; i++)
        {
            ref var existingOps = ref frozen.OpsPool[i];
            if (existingOps.IsEmpty) continue;
            var entity = frozen.OpsEntityByPoolIndex[i];

            EmitOpFromFrozen(frozen.Slabs, in existingOps.Value0, entity, delta);
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
        {
            delta.DestroyedEntities.Add(frozen.ExistingDestroyEntities[i]);
        }

        var copiedBytes = delta.DeepCopyOwnedData();

        return (delta, copiedBytes);
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
        return csIdx >= 0 && frozen.CreatedStatePool[csIdx].Destroyed;
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
        switch (slot.Kind)
        {
            case OpKindAdd:
            case OpKindSet:
                world.ApplyRawAddOrSet(entity, slot.ComponentType, slabs[slot.SlabIndex], slot.DataOffset);
                break;
            case OpKindRemove:
                world.RemoveBoxed(entity, slot.ComponentType);
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
                delta.AddCommands.Add(new RawComponentCommand(entity, slot.ComponentType, slot.DataOffset, slot.DataSize, slabs[slot.SlabIndex]));
                break;
            case OpKindSet:
                delta.SetCommands.Add(new RawComponentCommand(entity, slot.ComponentType, slot.DataOffset, slot.DataSize, slabs[slot.SlabIndex]));
                break;
            case OpKindRemove:
                delta.RemoveCommands.Add(new RawRemoveCommand(entity, slot.ComponentType));
                break;
        }
    }

    private (Archetype Archetype, int Count, ComponentType[] Types) BuildCreatedEntityComponents(in CreatedState state, out CreatedComponent[] sources)
    {
        EnsureArchetypeCacheValid();

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

        // Archetype lookup via bounded multi-entry cache
        var archetype = LookupArchetypeCache(types, idx);
        if (archetype != null)
            goto ArchetypeResolved;

        // True cache miss: allocate temporary array + Signature, look up in World
        {
            var comps = new ComponentType[idx];
            Array.Copy(types, comps, idx);
            archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));
            InsertArchetypeCache(types, idx, archetype);
        }
    ArchetypeResolved:

        return (archetype, idx, types!);
    }

    /// <summary>
    /// Probes the bounded archetype cache for an exact match.
    /// Returns null if no entry matches.
    /// </summary>
    private Archetype? LookupArchetypeCache(ComponentType[] types, int count)
    {
        EnsureArchetypeCacheValid();

        var cache = _archetypeCache;
        for (var i = 0; i < _archetypeCacheCount; i++)
        {
            ref readonly var entry = ref cache[i];
            if (entry.ComponentCount != count)
                continue;

            // Quick hash reject
            var hash = ComputeComponentHash(types, count);
            if (hash != entry.Hash)
                continue;

            // Exact compare against the archetype's signature
            var sig = entry.Archetype.Signature.AsSpan();
            for (var h = 0; h < count; h++)
            {
                if (types[h].Value != sig[h].Value)
                    goto NextEntry;
            }
            return entry.Archetype;
        NextEntry:;
        }
        return null;
    }

    /// <summary>
    /// Inserts an archetype into the bounded cache, evicting the oldest entry if full.
    /// </summary>
    private void InsertArchetypeCache(ComponentType[] types, int count, Archetype archetype)
    {
        EnsureArchetypeCacheValid();

        if (_archetypeCache.Length == 0)
            _archetypeCache = new ArchetypeCacheEntry[ArchetypeCacheSize];

        var hash = ComputeComponentHash(types, count);
        if (_archetypeCacheCount < _archetypeCache.Length)
        {
            _archetypeCache[_archetypeCacheCount++] = new ArchetypeCacheEntry(hash, count, archetype);
        }
        else
        {
            // Evict oldest (index 0) by shifting left
            Array.Copy(_archetypeCache, 1, _archetypeCache, 0, _archetypeCache.Length - 1);
            _archetypeCache[_archetypeCache.Length - 1] = new ArchetypeCacheEntry(hash, count, archetype);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeComponentHash(ComponentType[] types, int count)
    {
        var hash = 17;
        for (var i = 0; i < count; i++)
            hash = unchecked((hash * 31) + types[i].Value);
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureArchetypeCacheValid()
    {
        var generation = _world.CreateArchetypeCacheGeneration;
        if (_archetypeCacheGeneration == generation)
            return;

        _archetypeCacheGeneration = generation;
        _archetypeCacheCount = 0;
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
        var releasedCount = 0;
        var createdCount = 0;
        for (var i = 0; i < _createdStatePoolCount; i++)
        {
            ref readonly var s = ref _createdStatePool[i];
            if (s.Destroyed) releasedCount++;
            else createdCount++;
        }

        delta.ReservedEntities.EnsureCapacity(_createdStatePoolCount);
        delta.ReleasedEntities.EnsureCapacity(releasedCount);
        delta.CreatedEntities.EnsureCapacity(createdCount);
        delta.LinkCommands.EnsureCapacity(_hierarchyByChild.Count);
        delta.UnlinkCommands.EnsureCapacity(_hierarchyByChild.Count);
        delta.AddCommands.EnsureCapacity(_opsPoolCount);
        delta.SetCommands.EnsureCapacity(_opsPoolCount);
        delta.RemoveCommands.EnsureCapacity(_opsPoolCount);
        delta.DestroyedEntities.EnsureCapacity(_existingDestroyCount);

        for (var poolIdx = 0; poolIdx < _createdStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref _createdStatePool[poolIdx];
            var entity = _createdEntityByPoolIndex[poolIdx];
            delta.ReservedEntities.Add(entity);
            if (state.Destroyed)
            {
                delta.ReleasedEntities.Add(entity);
            }
            else if (state.Map.IsEmpty)
            {
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, Array.Empty<RawComponentValue>()));
            }
            else
            {
                var components = BuildCreatedEntityComponentsForDelta(in state);
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, components));
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
                delta.LinkCommands.Add(new LinkCommand(intent.Parent, child));
            }
            else
            {
                delta.UnlinkCommands.Add(new UnlinkCommand(child));
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
            delta.DestroyedEntities.Add(_existingDestroyEntities[i]);
        }
    }

    private void Clear()
    {
        _existingDestroyCount = 0;
        for (int i = 0; i < _opsPoolCount; i++)
        {
            _opsPool[i].Clear();
        }
        for (int i = 0; i < _maxOpsEntityId; i++)
        {
            if (i < _opsLookup.Length) _opsLookup[i] = -1;
        }
        _opsPoolCount = 0;
        _maxOpsEntityId = 0;
        for (int i = 0; i < _createdStatePoolCount; i++)
        {
            _createdStatePool[i].Map.Clear();
        }

        for (int i = 0; i < _maxCreatedEntityId; i++)
        {
            if (i < _createdStateLookup.Length) _createdStateLookup[i] = -1;
        }

        _createdStatePoolCount = 0;
        _maxCreatedEntityId = 0;
        _hierarchyByChild.Clear();
        _hasCreatedEntities = false;
        _opsOverflow.Clear();
        _createdOverflow.Clear();

        foreach (var slab in _slabs)
        {
            ArrayPool<byte>.Shared.Return(slab);
        }

        _slabs.Clear();
        _currentSlabIndex = -1;
        _currentSlabOffset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSlabSpace(int size)
    {
        if (_currentSlabIndex < 0 || _currentSlabOffset + size > _slabs[_currentSlabIndex].Length)
        {
            var slabSize = size > DefaultSlabSize ? size : DefaultSlabSize;
            var newSlab = ArrayPool<byte>.Shared.Rent(slabSize);
            _slabs.Add(newSlab);
            _currentSlabIndex = _slabs.Count - 1;
            _currentSlabOffset = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyData<T>(T component, int size, out int slabIndex, out int offset)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetComponentTypeId<T>()
    {
        return Component<T>.ComponentType.Value;
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
                delta.AddCommands.Add(new RawComponentCommand(entity, slot.ComponentType, slot.DataOffset, slot.DataSize, _slabs[slot.SlabIndex]));
                break;
            case OpKindSet:
                delta.SetCommands.Add(new RawComponentCommand(entity, slot.ComponentType, slot.DataOffset, slot.DataSize, _slabs[slot.SlabIndex]));
                break;
            case OpKindRemove:
                delta.RemoveCommands.Add(new RawRemoveCommand(entity, slot.ComponentType));
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyOpDirect(EntityOpSlot slot, Entity entity)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
            case OpKindSet:
                _world.ApplyRawAddOrSet(entity, slot.ComponentType, _slabs[slot.SlabIndex], slot.DataOffset);
                break;
            case OpKindRemove:
                _world.RemoveBoxed(entity, slot.ComponentType);
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
            if (idx >= 0) return idx;
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
            if (_opsPoolCount > 0)
            {
                Array.Copy(_opsPool, newPool, _opsPoolCount);
                Array.Copy(_opsEntityByPoolIndex, newEntities, _opsPoolCount);
            }
            _opsPool = newPool;
            _opsEntityByPoolIndex = newEntities;
        }

        var index = _opsPoolCount++;
        _opsPool[index].OverflowHead = -1;
        _opsEntityByPoolIndex[index] = entity;
        _opsLookup[id] = index;
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
