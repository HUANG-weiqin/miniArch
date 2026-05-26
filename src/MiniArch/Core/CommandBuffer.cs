using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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
    private readonly CommandBufferEntityAllocator _allocator;
    private ExistingEntityOps[] _opsPool = Array.Empty<ExistingEntityOps>();
    private Entity[] _opsEntityByPoolIndex = Array.Empty<Entity>();
    private int _opsPoolCount;
    private int[] _opsLookup = Array.Empty<int>();
    private int _maxOpsEntityId;
    private HashSet<Entity> _existingDestroys = new();
    private CreatedState[] _createdStatePool = Array.Empty<CreatedState>();
    private Entity[] _createdEntityByPoolIndex = Array.Empty<Entity>();
    private int _createdStatePoolCount;
    private int[] _createdStateLookup = Array.Empty<int>();
    private int _maxCreatedEntityId;
    private Dictionary<Entity, HierarchyIntent> _hierarchyByChild = new();
    private Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> _typeInfoCache = new();
    private List<byte[]> _slabs = new();
    private readonly List<(int ComponentTypeId, CreatedComponent Component)> _tempComponents = new();
    private bool _hasCreatedEntities;
    private int _currentSlabIndex = -1;
    private int _currentSlabOffset;

    /// <summary>
    /// Creates a buffer for a world.
    /// </summary>
    public CommandBuffer(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _allocator = new CommandBufferEntityAllocator(world);
    }

    /// <summary>
    /// Records an entity creation.
    /// </summary>
    public Entity Create()
    {
        var entity = _allocator.ReserveEntity();

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
        return entity;
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
                state.Set(componentTypeId, new CreatedComponent(info.RuntimeType, info.ComponentType, slabIndex, offset, info.Size));
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        var slot = new EntityOpSlot { ComponentTypeId = componentTypeId, Kind = OpKindAdd, AddSetData = new AddSetEntry(info.ComponentType, info.RuntimeType, slabIndex, offset, info.Size, info.Writer) };
        ops.SetOp(componentTypeId, slot);
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
                state.Set(componentTypeId, new CreatedComponent(info.RuntimeType, info.ComponentType, slabIndex, offset, info.Size));
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        var slot = new EntityOpSlot { ComponentTypeId = componentTypeId, Kind = OpKindSet, AddSetData = new AddSetEntry(info.ComponentType, info.RuntimeType, slabIndex, offset, info.Size, info.Writer) };
        ops.SetOp(componentTypeId, slot);
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
                state.Remove(componentTypeId);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        ops.RemoveOp(componentTypeId);
        var info = ResolveTypeInfo(componentTypeId);
        var slot = new EntityOpSlot { ComponentTypeId = componentTypeId, Kind = OpKindRemove, RemoveComponentType = info.ComponentType };
        ops.SetOp(componentTypeId, slot);
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
                return;
            }
        }

        _existingDestroys.Add(entity);
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
            _existingDestroys.Count == 0 && _createdStatePoolCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return false;
        }

        _world.BeginDeferredLayoutUpdates();
        try
        {
            for (var i = 0; i < _createdStatePoolCount; i++)
            {
                ref readonly var state = ref _createdStatePool[i];
                var entity = _createdEntityByPoolIndex[i];
                if (state.Destroyed)
                {
                    _world.ReleaseReservedEntity(entity);
                }
                else if (state.Count == 0 && state.Overflow is null)
                {
                    _world.MaterializeReservedEntityTrusted(entity, Signature.Empty, Array.Empty<RawComponentValue>());
                }
                else
                {
                    var (signature, components) = BuildCreatedEntityComponents(in state);
                    _world.MaterializeReservedEntityTrusted(entity, signature, components);
                }
            }

            for (var i = 0; i < _opsPoolCount; i++)
            {
                ref var existingOps = ref _opsPool[i];
                if (existingOps.Count == 0 && existingOps.Overflow is null) continue;
                var entity = _opsEntityByPoolIndex[i];

                if (existingOps.Count >= 1) ApplyOpDirect(existingOps.Slot0, entity);
                if (existingOps.Count >= 2) ApplyOpDirect(existingOps.Slot1, entity);
                if (existingOps.Count >= 3) ApplyOpDirect(existingOps.Slot2, entity);
                if (existingOps.Count >= 4) ApplyOpDirect(existingOps.Slot3, entity);
                if (existingOps.Overflow is not null)
                {
                    foreach (var kv in existingOps.Overflow)
                        ApplyOpDirect(kv.Value, entity);
                }
            }

            foreach (var (child, intent) in _hierarchyByChild)
            {
                if (_existingDestroys.Contains(child)) continue;
                if (_hasCreatedEntities)
                {
                    var csIdx = GetCreatedStateIndex(child);
                    if (csIdx >= 0 && _createdStatePool[csIdx].Destroyed) continue;
                }

                if (intent.IsLinked)
                    _world.Link(intent.Parent, child);
                else
                    _world.Unlink(child);
            }

            foreach (var entity in _existingDestroys)
            {
                if (_world.IsAlive(entity))
                    _world.Destroy(entity);
            }
        }
        finally
        {
            _world.EndDeferredLayoutUpdates();
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
        delta.DeepCopyOwnedData();
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
            _existingDestroys.Count == 0 && _createdStatePoolCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return Task.FromResult(new FrameDelta());
        }

        var frozen = SwapOutState();
        var task = Task.Run(() => BuildFromFrozen(frozen));

        SubmitFromFrozen(frozen);

        return task;
    }

    private FrozenBufferState SwapOutState()
    {
        var frozen = new FrozenBufferState
        {
            CreatedStatePool = _createdStatePool,
            CreatedStatePoolCount = _createdStatePoolCount,
            CreatedEntityByPoolIndex = _createdEntityByPoolIndex,
            CreatedStateLookup = _createdStateLookup,
            MaxCreatedEntityId = _maxCreatedEntityId,
            OpsPool = _opsPool,
            OpsPoolCount = _opsPoolCount,
            OpsEntityByPoolIndex = _opsEntityByPoolIndex,
            OpsLookup = _opsLookup,
            MaxOpsEntityId = _maxOpsEntityId,
            ExistingDestroys = _existingDestroys,
            HierarchyByChild = _hierarchyByChild,
            Slabs = _slabs,
            HasCreatedEntities = _hasCreatedEntities,
            TypeInfoCache = _typeInfoCache,
        };

        _createdStatePool = Array.Empty<CreatedState>();
        _createdStatePoolCount = 0;
        _createdEntityByPoolIndex = Array.Empty<Entity>();
        _createdStateLookup = Array.Empty<int>();
        _maxCreatedEntityId = 0;
        _opsPool = Array.Empty<ExistingEntityOps>();
        _opsPoolCount = 0;
        _opsEntityByPoolIndex = Array.Empty<Entity>();
        _opsLookup = Array.Empty<int>();
        _maxOpsEntityId = 0;
        _existingDestroys = new HashSet<Entity>();
        _hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        _slabs = new List<byte[]>();
        _hasCreatedEntities = false;
        _typeInfoCache = new Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)>();
        _currentSlabIndex = -1;
        _currentSlabOffset = 0;

        return frozen;
    }

    private void SubmitFromFrozen(FrozenBufferState frozen)
    {
        _world.BeginDeferredLayoutUpdates();
        try
        {
            for (var i = 0; i < frozen.CreatedStatePoolCount; i++)
            {
                ref readonly var state = ref frozen.CreatedStatePool[i];
                var entity = frozen.CreatedEntityByPoolIndex[i];
                if (state.Destroyed)
                {
                    _world.ReleaseReservedEntity(entity);
                }
                else if (state.Count == 0 && state.Overflow is null)
                {
                    _world.MaterializeReservedEntityTrusted(entity, Signature.Empty, Array.Empty<RawComponentValue>());
                }
                else
                {
                    _tempComponents.Clear();
                    state.CopyTo(_tempComponents);
                    var componentCount = _tempComponents.Count;

                    var components = ArrayPool<ComponentType>.Shared.Rent(componentCount);
                    var sourceComponents = ArrayPool<CreatedComponent>.Shared.Rent(componentCount);
                    try
                    {
                        for (var j = 0; j < componentCount; j++)
                        {
                            sourceComponents[j] = _tempComponents[j].Component;
                            components[j] = _tempComponents[j].Component.ComponentType;
                        }

                        Array.Sort(components, sourceComponents, 0, componentCount);

                        var rawComponents = new RawComponentValue[componentCount];
                        var signatureComponents = new ComponentType[componentCount];
                        for (var j = 0; j < componentCount; j++)
                        {
                            var sc = sourceComponents[j];
                            rawComponents[j] = new RawComponentValue(sc.ComponentType.Value, sc.RuntimeType, sc.ComponentType, frozen.Slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
                            signatureComponents[j] = sc.ComponentType;
                        }

                        _world.MaterializeReservedEntityTrusted(entity, Signature.CreateNormalized(signatureComponents), rawComponents);
                    }
                    finally
                    {
                        ArrayPool<CreatedComponent>.Shared.Return(sourceComponents);
                        ArrayPool<ComponentType>.Shared.Return(components);
                    }
                }
            }

            for (var i = 0; i < frozen.OpsPoolCount; i++)
            {
                ref var existingOps = ref frozen.OpsPool[i];
                if (existingOps.Count == 0 && existingOps.Overflow is null) continue;
                var entity = frozen.OpsEntityByPoolIndex[i];

                ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Slot0, entity);
                if (existingOps.Count >= 2) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Slot1, entity);
                if (existingOps.Count >= 3) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Slot2, entity);
                if (existingOps.Count >= 4) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Slot3, entity);
                if (existingOps.Overflow is not null)
                {
                    foreach (var kv in existingOps.Overflow)
                        ApplyOpDirectFromFrozen(_world, frozen.Slabs, kv.Value, entity);
                }
            }

            foreach (var (child, intent) in frozen.HierarchyByChild)
            {
                if (frozen.ExistingDestroys.Contains(child)) continue;
                if (frozen.HasCreatedEntities)
                {
                    var id = child.Id;
                    if ((uint)id < (uint)frozen.CreatedStateLookup.Length)
                    {
                        var csIdx = frozen.CreatedStateLookup[id];
                        if (csIdx >= 0 && frozen.CreatedStatePool[csIdx].Destroyed) continue;
                    }
                }

                if (intent.IsLinked)
                    _world.Link(intent.Parent, child);
                else
                    _world.Unlink(child);
            }

            foreach (var entity in frozen.ExistingDestroys)
            {
                if (_world.IsAlive(entity))
                    _world.Destroy(entity);
            }
        }
        finally
        {
            _world.EndDeferredLayoutUpdates();
        }
    }

    private static FrameDelta BuildFromFrozen(FrozenBufferState frozen)
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
        delta.DestroyedEntities.EnsureCapacity(frozen.ExistingDestroys.Count);

        for (var poolIdx = 0; poolIdx < frozen.CreatedStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref frozen.CreatedStatePool[poolIdx];
            var entity = frozen.CreatedEntityByPoolIndex[poolIdx];
            delta.ReservedEntities.Add(entity);
            if (state.Destroyed)
            {
                delta.ReleasedEntities.Add(entity);
            }
            else if (state.Count == 0 && state.Overflow is null)
            {
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, Signature.Empty, Array.Empty<RawComponentValue>()));
            }
            else
            {
                var (signature, components) = BuildCreatedEntityComponentsFromFrozen(in state, frozen.Slabs);
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, signature, components));
            }
        }

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (frozen.ExistingDestroys.Contains(child)) continue;
            if (frozen.HasCreatedEntities)
            {
                var id = child.Id;
                if ((uint)id < (uint)frozen.CreatedStateLookup.Length)
                {
                    var csIdx = frozen.CreatedStateLookup[id];
                    if (csIdx >= 0 && frozen.CreatedStatePool[csIdx].Destroyed) continue;
                }
            }

            if (intent.IsLinked)
                delta.LinkCommands.Add(new LinkCommand(intent.Parent, child));
            else
                delta.UnlinkCommands.Add(new UnlinkCommand(child));
        }

        for (var i = 0; i < frozen.OpsPoolCount; i++)
        {
            ref var existingOps = ref frozen.OpsPool[i];
            if (existingOps.Count == 0 && existingOps.Overflow is null) continue;
            var entity = frozen.OpsEntityByPoolIndex[i];

            EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Slot0, entity, delta);
            if (existingOps.Count >= 2) EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Slot1, entity, delta);
            if (existingOps.Count >= 3) EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Slot2, entity, delta);
            if (existingOps.Count >= 4) EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Slot3, entity, delta);
            if (existingOps.Overflow is not null)
            {
                foreach (var kv in existingOps.Overflow)
                {
                    var overflowSlot = kv.Value;
                    EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in overflowSlot, entity, delta);
                }
            }
        }

        foreach (var entity in frozen.ExistingDestroys)
        {
            delta.DestroyedEntities.Add(entity);
        }

        delta.DeepCopyOwnedData();

        foreach (var slab in frozen.Slabs)
        {
            ArrayPool<byte>.Shared.Return(slab);
        }

        return delta;
    }

    private static (Signature Signature, RawComponentValue[] Components) BuildCreatedEntityComponentsFromFrozen(
        in CreatedState state, List<byte[]> slabs)
    {
        var componentCount = state.Count;
        if (state.Overflow is not null) componentCount += state.Overflow.Count;

        var types = ArrayPool<ComponentType>.Shared.Rent(componentCount);
        var sources = ArrayPool<CreatedComponent>.Shared.Rent(componentCount);
        var typeIdAndComp = ArrayPool<(int ComponentTypeId, CreatedComponent Component)>.Shared.Rent(componentCount);
        try
        {
            var idx = 0;
            if (state.Count >= 1) typeIdAndComp[idx++] = (state.ComponentTypeId0, state.Component0);
            if (state.Count >= 2) typeIdAndComp[idx++] = (state.ComponentTypeId1, state.Component1);
            if (state.Count >= 3) typeIdAndComp[idx++] = (state.ComponentTypeId2, state.Component2);
            if (state.Count >= 4) typeIdAndComp[idx++] = (state.ComponentTypeId3, state.Component3);
            if (state.Overflow is not null)
            {
                foreach (var kv in state.Overflow)
                    typeIdAndComp[idx++] = (kv.Key, kv.Value);
            }

            for (var i = 0; i < idx; i++)
            {
                sources[i] = typeIdAndComp[i].Component;
                types[i] = typeIdAndComp[i].Component.ComponentType;
            }

            Array.Sort(types, sources, 0, idx);

            var rawComponents = new RawComponentValue[idx];
            var signatureComponents = new ComponentType[idx];
            for (var i = 0; i < idx; i++)
            {
                var sc = sources[i];
                rawComponents[i] = new RawComponentValue(sc.ComponentType.Value, sc.RuntimeType, sc.ComponentType, slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
                signatureComponents[i] = sc.ComponentType;
            }

            return (Signature.CreateNormalized(signatureComponents), rawComponents);
        }
        finally
        {
            ArrayPool<(int, CreatedComponent)>.Shared.Return(typeIdAndComp);
            ArrayPool<CreatedComponent>.Shared.Return(sources);
            ArrayPool<ComponentType>.Shared.Return(types);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyOpDirectFromFrozen(World world, List<byte[]> slabs, EntityOpSlot slot, Entity entity)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
            case OpKindSet:
            {
                var d = slot.AddSetData;
                world.ApplyRawAddOrSet(entity, d.ComponentType, d.RuntimeType, slabs[d.SlabIndex], d.DataOffset, d.Writer);
                break;
            }
            case OpKindRemove:
                world.RemoveBoxed(entity, slot.RemoveComponentType);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitOpFromFrozen(
        List<byte[]> slabs,
        Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> typeInfoCache,
        in EntityOpSlot slot, Entity entity, FrameDelta delta)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
                delta.AddCommands.Add(new RawComponentCommand(entity, slot.ComponentTypeId, slot.AddSetData.RuntimeType, slot.AddSetData.ComponentType, slot.AddSetData.DataOffset, slot.AddSetData.DataSize, slot.AddSetData.Writer, slabs[slot.AddSetData.SlabIndex]));
                break;
            case OpKindSet:
                delta.SetCommands.Add(new RawComponentCommand(entity, slot.ComponentTypeId, slot.AddSetData.RuntimeType, slot.AddSetData.ComponentType, slot.AddSetData.DataOffset, slot.AddSetData.DataSize, slot.AddSetData.Writer, slabs[slot.AddSetData.SlabIndex]));
                break;
            case OpKindRemove:
            {
                var info = typeInfoCache[slot.ComponentTypeId];
                delta.RemoveCommands.Add(new RawRemoveCommand(entity, slot.ComponentTypeId, info.RuntimeType, slot.RemoveComponentType));
                break;
            }
        }
    }

    private (Signature Signature, RawComponentValue[] Components) BuildCreatedEntityComponents(in CreatedState state)
    {
        _tempComponents.Clear();
        state.CopyTo(_tempComponents);
        var componentCount = _tempComponents.Count;

        var components = ArrayPool<ComponentType>.Shared.Rent(componentCount);
        var sourceComponents = ArrayPool<CreatedComponent>.Shared.Rent(componentCount);
        try
        {
            for (var i = 0; i < componentCount; i++)
            {
                sourceComponents[i] = _tempComponents[i].Component;
                components[i] = _tempComponents[i].Component.ComponentType;
            }

            Array.Sort(components, sourceComponents, 0, componentCount);

            var rawComponents = new RawComponentValue[componentCount];
            var signatureComponents = new ComponentType[componentCount];
            for (var i = 0; i < componentCount; i++)
            {
                var sc = sourceComponents[i];
                rawComponents[i] = new RawComponentValue(ComponentsTypeToId(sc.ComponentType), sc.RuntimeType, sc.ComponentType, _slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
                signatureComponents[i] = sc.ComponentType;
            }

            return (Signature.CreateNormalized(signatureComponents), rawComponents);
        }
        finally
        {
            ArrayPool<CreatedComponent>.Shared.Return(sourceComponents);
            ArrayPool<ComponentType>.Shared.Return(components);
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
        delta.DestroyedEntities.EnsureCapacity(_existingDestroys.Count);

        for (var poolIdx = 0; poolIdx < _createdStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref _createdStatePool[poolIdx];
            var entity = _createdEntityByPoolIndex[poolIdx];
            delta.ReservedEntities.Add(entity);
            if (state.Destroyed)
            {
                delta.ReleasedEntities.Add(entity);
            }
            else if (state.Count == 0 && state.Overflow is null)
            {
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, Signature.Empty, Array.Empty<RawComponentValue>()));
            }
            else
            {
                var (signature, components) = BuildCreatedEntityComponents(in state);
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, signature, components));
            }
        }

        foreach (var (child, intent) in _hierarchyByChild)
        {
            if (_existingDestroys.Contains(child))
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
            if (existingOps.Count == 0 && existingOps.Overflow is null) continue;
            var entity = _opsEntityByPoolIndex[i];

            if (existingOps.Count >= 1) EmitOp(in existingOps.Slot0, entity, delta);
            if (existingOps.Count >= 2) EmitOp(in existingOps.Slot1, entity, delta);
            if (existingOps.Count >= 3) EmitOp(in existingOps.Slot2, entity, delta);
            if (existingOps.Count >= 4) EmitOp(in existingOps.Slot3, entity, delta);
            if (existingOps.Overflow is not null)
            {
                foreach (var kv in existingOps.Overflow)
                {
                    var overflowSlot = kv.Value;
                    EmitOp(in overflowSlot, entity, delta);
                }
            }
        }

        foreach (var entity in _existingDestroys)
        {
            delta.DestroyedEntities.Add(entity);
        }
    }

    private void Clear()
    {
        _existingDestroys.Clear();
        for (int i = 0; i < _opsPoolCount; i++)
        {
            _opsPool[i].Overflow?.Clear();
            _opsPool[i] = default;
        }
        for (int i = 0; i < _maxOpsEntityId; i++)
        {
            if (i < _opsLookup.Length) _opsLookup[i] = -1;
        }
        _opsPoolCount = 0;
        _maxOpsEntityId = 0;
        for (int i = 0; i < _createdStatePoolCount; i++)
        {
            _createdStatePool[i].Overflow?.Clear();
            _createdStatePool[i] = default;
        }

        for (int i = 0; i < _maxCreatedEntityId; i++)
        {
            if (i < _createdStateLookup.Length) _createdStateLookup[i] = -1;
        }

        _createdStatePoolCount = 0;
        _maxCreatedEntityId = 0;
        _hierarchyByChild.Clear();
        _hasCreatedEntities = false;

        foreach (var slab in _slabs)
        {
            ArrayPool<byte>.Shared.Return(slab);
        }

        _slabs.Clear();
        _currentSlabIndex = -1;
        _currentSlabOffset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyData<T>(T component, int size, out int slabIndex, out int offset)
    {
        if (_currentSlabIndex < 0 || _currentSlabOffset + size > _slabs[_currentSlabIndex].Length)
        {
            var slabSize = size > DefaultSlabSize ? size : DefaultSlabSize;
            var newSlab = ArrayPool<byte>.Shared.Rent(slabSize);
            _slabs.Add(newSlab);
            _currentSlabIndex = _slabs.Count - 1;
            _currentSlabOffset = 0;
        }

        slabIndex = _currentSlabIndex;
        offset = _currentSlabOffset;
        _currentSlabOffset += size;

        fixed (byte* ptr = &_slabs[slabIndex][offset])
        {
            Unsafe.Write(ptr, component);
        }
    }

    private (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer) ResolveTypeInfo(int componentTypeId)
    {
        if (_typeInfoCache.TryGetValue(componentTypeId, out var info))
        {
            return info;
        }

        var componentType = (ComponentType)componentTypeId;
        if (!_world.Components.TryGetType(componentType, out var runtimeType))
        {
            runtimeType = typeof(object);
            componentType = default;
        }

        var size = runtimeType == typeof(object) ? 0 : ComponentWriterCache.GetSize(runtimeType);
        var writer = runtimeType == typeof(object) ? null! : ComponentWriterCache.GetColumnWriter(runtimeType);
        info = (runtimeType, componentType, size, writer);
        _typeInfoCache.Add(componentTypeId, info);
        return info;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetComponentTypeId<T>()
    {
        var registry = _world.Components;
        var entry = Volatile.Read(ref ComponentTypeCache<T>.Entry);
        if (entry is not null && ReferenceEquals(entry.Registry, registry))
        {
            return entry.ComponentTypeId;
        }

        var componentTypeId = registry.GetOrCreate<T>().Value;
        Volatile.Write(ref ComponentTypeCache<T>.Entry, new ComponentTypeIdCacheEntry(registry, componentTypeId));
        return componentTypeId;
    }

    private int ComponentsTypeToId(ComponentType componentType)
    {
        return componentType.Value;
    }

    private const byte OpKindNone = 0;
    private const byte OpKindAdd = 1;
    private const byte OpKindSet = 2;
    private const byte OpKindRemove = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitOp(in EntityOpSlot slot, Entity entity, FrameDelta delta)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
                delta.AddCommands.Add(new RawComponentCommand(entity, slot.ComponentTypeId, slot.AddSetData.RuntimeType, slot.AddSetData.ComponentType, slot.AddSetData.DataOffset, slot.AddSetData.DataSize, slot.AddSetData.Writer, _slabs[slot.AddSetData.SlabIndex]));
                break;
            case OpKindSet:
                delta.SetCommands.Add(new RawComponentCommand(entity, slot.ComponentTypeId, slot.AddSetData.RuntimeType, slot.AddSetData.ComponentType, slot.AddSetData.DataOffset, slot.AddSetData.DataSize, slot.AddSetData.Writer, _slabs[slot.AddSetData.SlabIndex]));
                break;
            case OpKindRemove:
            {
                var info = ResolveTypeInfo(slot.ComponentTypeId);
                delta.RemoveCommands.Add(new RawRemoveCommand(entity, slot.ComponentTypeId, info.RuntimeType, slot.RemoveComponentType));
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplyOpDirect(EntityOpSlot slot, Entity entity)
    {
        switch (slot.Kind)
        {
            case OpKindAdd:
            case OpKindSet:
            {
                var d = slot.AddSetData;
                _world.ApplyRawAddOrSet(entity, d.ComponentType, d.RuntimeType, _slabs[d.SlabIndex], d.DataOffset, d.Writer);
                break;
            }
            case OpKindRemove:
                _world.RemoveBoxed(entity, slot.RemoveComponentType);
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
            var newPool = new ExistingEntityOps[newSize];
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
        _opsEntityByPoolIndex[index] = entity;
        _opsLookup[id] = index;
        if (id >= _maxOpsEntityId) _maxOpsEntityId = id + 1;
        return index;
    }

    private struct EntityOpSlot
    {
        public int ComponentTypeId;
        public byte Kind;
        public AddSetEntry AddSetData;
        public ComponentType RemoveComponentType;
    }

    private struct ExistingEntityOps
    {
        public int Count;
        public EntityOpSlot Slot0;
        public EntityOpSlot Slot1;
        public EntityOpSlot Slot2;
        public EntityOpSlot Slot3;
        public Dictionary<int, EntityOpSlot>? Overflow;

        public void SetOp(int componentTypeId, EntityOpSlot slot)
        {
            if (Count >= 1 && Slot0.ComponentTypeId == componentTypeId) { Slot0 = slot; return; }
            if (Count >= 2 && Slot1.ComponentTypeId == componentTypeId) { Slot1 = slot; return; }
            if (Count >= 3 && Slot2.ComponentTypeId == componentTypeId) { Slot2 = slot; return; }
            if (Count >= 4 && Slot3.ComponentTypeId == componentTypeId) { Slot3 = slot; return; }

            switch (Count)
            {
                case 0: Slot0 = slot; Count = 1; return;
                case 1: Slot1 = slot; Count = 2; return;
                case 2: Slot2 = slot; Count = 3; return;
                case 3: Slot3 = slot; Count = 4; return;
                default:
                    Overflow ??= new Dictionary<int, EntityOpSlot>(4);
                    Overflow[componentTypeId] = slot;
                    return;
            }
        }

        public bool RemoveOp(int componentTypeId)
        {
            if (Count >= 1 && Slot0.ComponentTypeId == componentTypeId) { RemoveOpAt(0); return true; }
            if (Count >= 2 && Slot1.ComponentTypeId == componentTypeId) { RemoveOpAt(1); return true; }
            if (Count >= 3 && Slot2.ComponentTypeId == componentTypeId) { RemoveOpAt(2); return true; }
            if (Count >= 4 && Slot3.ComponentTypeId == componentTypeId) { RemoveOpAt(3); return true; }
            if (Overflow is not null) return Overflow.Remove(componentTypeId);
            return false;
        }

        private void RemoveOpAt(int index)
        {
            var last = Count - 1;
            switch (index)
            {
                case 0:
                    if (last >= 1) { Slot0 = Slot1; }
                    if (last >= 2) { Slot1 = Slot2; }
                    if (last >= 3) { Slot2 = Slot3; }
                    break;
                case 1:
                    if (last >= 2) { Slot1 = Slot2; }
                    if (last >= 3) { Slot2 = Slot3; }
                    break;
                case 2:
                    if (last >= 3) { Slot2 = Slot3; }
                    break;
            }
            Count = last;
        }
    }

    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);

    private struct AddSetEntry
    {
        public ComponentType ComponentType;
        public Type RuntimeType;
        public int SlabIndex;
        public int DataOffset;
        public int DataSize;
        public ComponentWriterCache.ColumnWriterDelegate Writer;

        public AddSetEntry(ComponentType componentType, Type runtimeType, int slabIndex, int dataOffset, int dataSize, ComponentWriterCache.ColumnWriterDelegate writer)
        {
            ComponentType = componentType;
            RuntimeType = runtimeType;
            SlabIndex = slabIndex;
            DataOffset = dataOffset;
            DataSize = dataSize;
            Writer = writer;
        }
    }

    private struct CreatedState
    {
        public int Count;
        public int ComponentTypeId0;
        public CreatedComponent Component0;
        public int ComponentTypeId1;
        public CreatedComponent Component1;
        public int ComponentTypeId2;
        public CreatedComponent Component2;
        public int ComponentTypeId3;
        public CreatedComponent Component3;
        public Dictionary<int, CreatedComponent>? Overflow;
        public bool Destroyed;

        public void Set(int componentTypeId, CreatedComponent component)
        {
            switch (Count)
            {
                case 0: ComponentTypeId0 = componentTypeId; Component0 = component; Count = 1; return;
                case 1 when ComponentTypeId0 == componentTypeId: Component0 = component; return;
                case 1: ComponentTypeId1 = componentTypeId; Component1 = component; Count = 2; return;
                case 2 when ComponentTypeId0 == componentTypeId: Component0 = component; return;
                case 2 when ComponentTypeId1 == componentTypeId: Component1 = component; return;
                case 2: ComponentTypeId2 = componentTypeId; Component2 = component; Count = 3; return;
                case 3 when ComponentTypeId0 == componentTypeId: Component0 = component; return;
                case 3 when ComponentTypeId1 == componentTypeId: Component1 = component; return;
                case 3 when ComponentTypeId2 == componentTypeId: Component2 = component; return;
                case 3: ComponentTypeId3 = componentTypeId; Component3 = component; Count = 4; return;
                default:
                    if (Count <= 4)
                    {
                        if (ComponentTypeId0 == componentTypeId) { Component0 = component; return; }
                        if (ComponentTypeId1 == componentTypeId) { Component1 = component; return; }
                        if (ComponentTypeId2 == componentTypeId) { Component2 = component; return; }
                        if (ComponentTypeId3 == componentTypeId) { Component3 = component; return; }
                        Count = 5;
                    }

                    Overflow ??= new Dictionary<int, CreatedComponent>(4);
                    Overflow[componentTypeId] = component;
                    return;
            }
        }

        public bool TryGetValue(int componentTypeId, out CreatedComponent component)
        {
            if (Count >= 1 && ComponentTypeId0 == componentTypeId) { component = Component0; return true; }
            if (Count >= 2 && ComponentTypeId1 == componentTypeId) { component = Component1; return true; }
            if (Count >= 3 && ComponentTypeId2 == componentTypeId) { component = Component2; return true; }
            if (Count >= 4 && ComponentTypeId3 == componentTypeId) { component = Component3; return true; }
            if (Overflow is not null) return Overflow.TryGetValue(componentTypeId, out component);
            component = default;
            return false;
        }

        public bool Remove(int componentTypeId)
        {
            if (Count >= 1 && ComponentTypeId0 == componentTypeId) { RemoveAt(0); return true; }
            if (Count >= 2 && ComponentTypeId1 == componentTypeId) { RemoveAt(1); return true; }
            if (Count >= 3 && ComponentTypeId2 == componentTypeId) { RemoveAt(2); return true; }
            if (Count >= 4 && ComponentTypeId3 == componentTypeId) { RemoveAt(3); return true; }
            if (Overflow is not null) return Overflow.Remove(componentTypeId);
            return false;
        }

        private void RemoveAt(int index)
        {
            var last = Count - 1;
            switch (index)
            {
                case 0:
                    if (last >= 1) { ComponentTypeId0 = ComponentTypeId1; Component0 = Component1; }
                    if (last >= 2) { ComponentTypeId1 = ComponentTypeId2; Component1 = Component2; }
                    if (last >= 3) { ComponentTypeId2 = ComponentTypeId3; Component2 = Component3; }
                    break;
                case 1:
                    if (last >= 2) { ComponentTypeId1 = ComponentTypeId2; Component1 = Component2; }
                    if (last >= 3) { ComponentTypeId2 = ComponentTypeId3; Component2 = Component3; }
                    break;
                case 2:
                    if (last >= 3) { ComponentTypeId2 = ComponentTypeId3; Component2 = Component3; }
                    break;
            }

            Count = last;
        }

        public void CopyTo(List<(int ComponentTypeId, CreatedComponent Component)> target)
        {
            if (Count >= 1) target.Add((ComponentTypeId0, Component0));
            if (Count >= 2) target.Add((ComponentTypeId1, Component1));
            if (Count >= 3) target.Add((ComponentTypeId2, Component2));
            if (Count >= 4) target.Add((ComponentTypeId3, Component3));
            if (Overflow is not null)
            {
                foreach (var kv in Overflow)
                    target.Add((kv.Key, kv.Value));
            }
        }
    }

    private readonly record struct CreatedComponent(Type RuntimeType, ComponentType ComponentType, int SlabIndex, int DataOffset, int DataSize);

    private static class ComponentTypeCache<T>
    {
        public static ComponentTypeIdCacheEntry? Entry;
    }

    private sealed record ComponentTypeIdCacheEntry(ComponentRegistry Registry, int ComponentTypeId);

    private sealed class FrozenBufferState
    {
        public CreatedState[] CreatedStatePool = null!;
        public int CreatedStatePoolCount;
        public Entity[] CreatedEntityByPoolIndex = null!;
        public int[] CreatedStateLookup = null!;
        public int MaxCreatedEntityId;

        public ExistingEntityOps[] OpsPool = null!;
        public int OpsPoolCount;
        public Entity[] OpsEntityByPoolIndex = null!;
        public int[] OpsLookup = null!;
        public int MaxOpsEntityId;

        public HashSet<Entity> ExistingDestroys = null!;
        public Dictionary<Entity, HierarchyIntent> HierarchyByChild = null!;
        public List<byte[]> Slabs = null!;
        public bool HasCreatedEntities;
        public Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> TypeInfoCache = null!;
    }
}
