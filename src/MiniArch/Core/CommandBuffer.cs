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
    private Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> _typeInfoCache = new();
    private List<byte[]> _slabs = new();
    private readonly List<(int ComponentTypeId, CreatedComponent Component)> _tempComponents = new();
    private List<(Entity Deferred, Entity Source)> _cloneCommands = new(4);
    private bool _hasCreatedEntities;
    private int _currentSlabIndex = -1;
    private int _currentSlabOffset;

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
                state.Map.Set(componentTypeId, new CreatedComponent(info.RuntimeType, info.ComponentType, slabIndex, offset, info.Size));
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        var slot = new EntityOpSlot { ComponentTypeId = componentTypeId, Kind = OpKindAdd, AddSetData = new AddSetEntry(info.ComponentType, info.RuntimeType, slabIndex, offset, info.Size, info.Writer) };
        ops.Set(componentTypeId, slot);
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
                state.Map.Set(componentTypeId, new CreatedComponent(info.RuntimeType, info.ComponentType, slabIndex, offset, info.Size));
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        var slot = new EntityOpSlot { ComponentTypeId = componentTypeId, Kind = OpKindSet, AddSetData = new AddSetEntry(info.ComponentType, info.RuntimeType, slabIndex, offset, info.Size, info.Writer) };
        ops.Set(componentTypeId, slot);
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
                state.Map.Remove(componentTypeId);
                return;
            }
        }

        var opsIdx = GetOrCreateOpsIndex(entity);
        ref var ops = ref _opsPool[opsIdx];
        ops.Remove(componentTypeId);
        var info = ResolveTypeInfo(componentTypeId);
        var slot = new EntityOpSlot { ComponentTypeId = componentTypeId, Kind = OpKindRemove, RemoveComponentType = info.ComponentType };
        ops.Set(componentTypeId, slot);
    }

    /// <summary>
    /// Records a destroy command.
    /// </summary>
    public void Destroy(Entity entity)
    {
        for (var i = _cloneCommands.Count - 1; i >= 0; i--)
        {
            if (_cloneCommands[i].Deferred == entity)
            {
                _cloneCommands.RemoveAt(i);
                if (_hasCreatedEntities)
                {
                    var createdIdx = GetCreatedStateIndex(entity);
                    if (createdIdx >= 0)
                    {
                        _createdStatePool[createdIdx].Destroyed = true;
                        return;
                    }
                }
                _world.ReleaseReservedEntity(entity);
                return;
            }
        }

        if (_hasCreatedEntities)
        {
            var createdIdx = GetCreatedStateIndex(entity);
            if (createdIdx >= 0)
            {
                _createdStatePool[createdIdx].Destroyed = true;
                return;
            }
        }

        AddExistingDestroy(entity);
    }

    /// <summary>
    /// Records a deep clone of an entity and its entire child subtree.
    /// Component data is read at commit time, not at record time.
    /// The clone root gets a new deferred entity handle; children are allocated at commit time.
    /// </summary>
    /// <param name="source">The entity to clone. Must be alive at record time.</param>
    /// <returns>A deferred entity handle that becomes alive after <see cref="Submit"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="source"/> is no longer alive.</exception>
    public Entity Clone(Entity source)
    {
        if (!_world.IsAlive(source))
        {
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");
        }

        var deferred = _world.ReserveDeferredEntity();
        RegisterInCreatedState(deferred);
        _cloneCommands.Add((deferred, source));
        return deferred;
    }

    private void ExpandCloneCommands()
    {
        if (_cloneCommands.Count == 0) return;

        for (var i = 0; i < _cloneCommands.Count; i++)
        {
            var (deferred, source) = _cloneCommands[i];
            ExpandCloneSubtree(deferred, source);
        }
        _cloneCommands.Clear();
    }

    private void ExpandCloneSubtree(Entity deferredRoot, Entity source)
    {
        if (!_world.TryGetLocation(source, out var location))
        {
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");
        }

        SnapshotEntityToCreatedState(deferredRoot, location);

        var stack = new List<(Entity SourceParent, Entity CloneParent, Entity SourceChild)>(4);
        var children = _world.GetChildren(source);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            stack.Add((source, deferredRoot, children[i]));
        }

        while (stack.Count > 0)
        {
            var last = stack.Count - 1;
            var (srcParent, cloneParent, srcChild) = stack[last];
            stack.RemoveAt(last);

            if (!_world.IsAlive(srcChild)) continue;

            var cloneChild = _world.ReserveDeferredEntity();
            RegisterInCreatedState(cloneChild);

            if (_world.TryGetLocation(srcChild, out var childLocation))
            {
                SnapshotEntityToCreatedState(cloneChild, childLocation);
            }

            _hierarchyByChild[cloneChild] = new HierarchyIntent(true, cloneParent);

            var grandChildren = _world.GetChildren(srcChild);
            for (var i = grandChildren.Count - 1; i >= 0; i--)
            {
                stack.Add((srcChild, cloneChild, grandChildren[i]));
            }
        }
    }

    private void SnapshotEntityToCreatedState(Entity deferred, EntityInfo location)
    {
        var createdIdx = GetCreatedStateIndex(deferred);
        if (createdIdx < 0) return;

        var archetype = location.Archetype;
        var chunk = archetype.GetChunk(location.ChunkIndex);
        var sourceRow = location.RowIndex;

        var components = archetype.Signature.AsSpan();
        for (var i = 0; i < components.Length; i++)
        {
            var componentType = components[i];
            var componentTypeId = componentType.Value;
            ref var state = ref _createdStatePool[createdIdx];
            if (state.Map.TryGetValue(componentTypeId, out _))
                continue;
            var info = ResolveTypeInfo(componentTypeId);
            CopyComponentFromChunk(chunk, i, sourceRow, info.Size, out var slabIndex, out var offset);
            state.Map.Set(componentTypeId, new CreatedComponent(info.RuntimeType, info.ComponentType, slabIndex, offset, info.Size));
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
        ExpandCloneCommands();
        if (_opsPoolCount == 0 &&
            _existingDestroyCount == 0 && _createdStatePoolCount == 0 &&
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
                else if (state.Map.Count == 0 && state.Map.Overflow is null)
                {
                    _world.MaterializeReservedEntityTrusted(entity, Signature.Empty, Array.Empty<RawComponentValue>());
                }
                else
                {
                    var (archetype, components, count) = BuildCreatedEntityComponents(in state);
                    try
                    {
                        _world.MaterializeReservedEntityDirect(entity, archetype, components.AsSpan(0, count));
                    }
                    finally
                    {
                        ArrayPool<RawComponentValue>.Shared.Return(components);
                    }
                }
            }

            for (var i = 0; i < _opsPoolCount; i++)
            {
                ref var existingOps = ref _opsPool[i];
                if (existingOps.Count == 0 && existingOps.Overflow is null) continue;
                var entity = _opsEntityByPoolIndex[i];

                if (existingOps.Count >= 1) ApplyOpDirect(existingOps.Value0, entity);
                if (existingOps.Count >= 2) ApplyOpDirect(existingOps.Value1, entity);
                if (existingOps.Count >= 3) ApplyOpDirect(existingOps.Value2, entity);
                if (existingOps.Count >= 4) ApplyOpDirect(existingOps.Value3, entity);
                if (existingOps.Overflow is not null)
                {
                    foreach (var kv in existingOps.Overflow)
                        ApplyOpDirect(kv.Value, entity);
                }
            }

            foreach (var (child, intent) in _hierarchyByChild)
            {
                if (FindExistingDestroy(child) >= 0) continue;
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

            for (var i = 0; i < _existingDestroyCount; i++)
            {
                var entity = _existingDestroyEntities[i];
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
        ExpandCloneCommands();
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
        ExpandCloneCommands();
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
            return t.Result;
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
            MaxCreatedEntityId = _maxCreatedEntityId,
            OpsPool = _opsPool,
            OpsPoolCount = _opsPoolCount,
            OpsEntityByPoolIndex = _opsEntityByPoolIndex,
            OpsLookup = _opsLookup,
            MaxOpsEntityId = _maxOpsEntityId,
            ExistingDestroyEntities = _existingDestroyEntities,
            ExistingDestroyCount = _existingDestroyCount,
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
                else if (state.Map.Count == 0 && state.Map.Overflow is null)
                {
                    _world.MaterializeReservedEntityTrusted(entity, Signature.Empty, Array.Empty<RawComponentValue>());
                }
                else
                {
                    _tempComponents.Clear();
                    state.Map.CopyTo(_tempComponents);
                    var componentCount = _tempComponents.Count;

                    var components = ArrayPool<ComponentType>.Shared.Rent(componentCount);
                    var sourceComponents = ArrayPool<CreatedComponent>.Shared.Rent(componentCount);
                    var rawComponents = ArrayPool<RawComponentValue>.Shared.Rent(componentCount);
                    try
                    {
                        for (var j = 0; j < componentCount; j++)
                        {
                            sourceComponents[j] = _tempComponents[j].Component;
                            components[j] = _tempComponents[j].Component.ComponentType;
                        }

                        Array.Sort(components, sourceComponents, 0, componentCount);

                        var key = new World.CreateArchetypeKey(components.AsSpan(0, componentCount));
                        var archetype = _world.GetOrCreateArchetype(key);

                        for (var j = 0; j < componentCount; j++)
                        {
                            var sc = sourceComponents[j];
                            rawComponents[j] = new RawComponentValue(sc.ComponentType.Value, sc.RuntimeType, sc.ComponentType, frozen.Slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
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
                if (existingOps.Count == 0 && existingOps.Overflow is null) continue;
                var entity = frozen.OpsEntityByPoolIndex[i];

                if (existingOps.Count >= 1) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value0, entity);
                if (existingOps.Count >= 2) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value1, entity);
                if (existingOps.Count >= 3) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value2, entity);
                if (existingOps.Count >= 4) ApplyOpDirectFromFrozen(_world, frozen.Slabs, existingOps.Value3, entity);
                if (existingOps.Overflow is not null)
                {
                    foreach (var kv in existingOps.Overflow)
                        ApplyOpDirectFromFrozen(_world, frozen.Slabs, kv.Value, entity);
                }
            }

            foreach (var (child, intent) in frozen.HierarchyByChild)
            {
                if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, child) >= 0) continue;
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

            for (var i = 0; i < frozen.ExistingDestroyCount; i++)
            {
                var entity = frozen.ExistingDestroyEntities[i];
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
            else if (state.Map.Count == 0 && state.Map.Overflow is null)
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
            if (FindExistingDestroy(frozen.ExistingDestroyEntities, frozen.ExistingDestroyCount, child) >= 0) continue;
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

            EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Value0, entity, delta);
            if (existingOps.Count >= 2) EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Value1, entity, delta);
            if (existingOps.Count >= 3) EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Value2, entity, delta);
            if (existingOps.Count >= 4) EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in existingOps.Value3, entity, delta);
            if (existingOps.Overflow is not null)
            {
                foreach (var kv in existingOps.Overflow)
                {
                    var overflowSlot = kv.Value;
                    EmitOpFromFrozen(frozen.Slabs, frozen.TypeInfoCache, in overflowSlot, entity, delta);
                }
            }
        }

        for (var i = 0; i < frozen.ExistingDestroyCount; i++)
        {
            delta.DestroyedEntities.Add(frozen.ExistingDestroyEntities[i]);
        }

        delta.DeepCopyOwnedData();

        return delta;
    }

    private static (ComponentType[] Types, CreatedComponent[] Sources, int Count) ExtractAndSortComponents(
        in CreatedState state)
    {
        var count = state.Map.Count;
        if (state.Map.Overflow is not null) count += state.Map.Overflow.Count;

        var types = ArrayPool<ComponentType>.Shared.Rent(count);
        var sources = ArrayPool<CreatedComponent>.Shared.Rent(count);

        var idx = 0;
        if (state.Map.Count >= 1) { sources[idx] = state.Map.Value0; types[idx] = state.Map.Value0.ComponentType; idx++; }
        if (state.Map.Count >= 2) { sources[idx] = state.Map.Value1; types[idx] = state.Map.Value1.ComponentType; idx++; }
        if (state.Map.Count >= 3) { sources[idx] = state.Map.Value2; types[idx] = state.Map.Value2.ComponentType; idx++; }
        if (state.Map.Count >= 4) { sources[idx] = state.Map.Value3; types[idx] = state.Map.Value3.ComponentType; idx++; }
        if (state.Map.Overflow is not null)
        {
            foreach (var kv in state.Map.Overflow)
            {
                sources[idx] = kv.Value;
                types[idx] = kv.Value.ComponentType;
                idx++;
            }
        }

        Array.Sort(types, sources, 0, idx);
        return (types, sources, idx);
    }

    private static void ReturnExtracted(ComponentType[] types, CreatedComponent[] sources)
    {
        ArrayPool<CreatedComponent>.Shared.Return(sources);
        ArrayPool<ComponentType>.Shared.Return(types);
    }

    private static (Signature Signature, RawComponentValue[] Components) BuildCreatedEntityComponentsFromFrozen(
        in CreatedState state, List<byte[]> slabs)
    {
        var (types, sources, count) = ExtractAndSortComponents(in state);
        try
        {
            var rawComponents = new RawComponentValue[count];
            var signatureComponents = new ComponentType[count];
            for (var i = 0; i < count; i++)
            {
                var sc = sources[i];
                rawComponents[i] = new RawComponentValue(sc.ComponentType.Value, sc.RuntimeType, sc.ComponentType, slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
                signatureComponents[i] = sc.ComponentType;
            }
            return (Signature.CreateNormalized(signatureComponents), rawComponents);
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

    private (Archetype Archetype, RawComponentValue[] Components, int Count) BuildCreatedEntityComponents(in CreatedState state)
    {
        var (types, sources, count) = ExtractAndSortComponents(in state);
        try
        {
            var key = new World.CreateArchetypeKey(types.AsSpan(0, count));
            var archetype = _world.GetOrCreateArchetype(key);

            var rawComponents = ArrayPool<RawComponentValue>.Shared.Rent(count);
            for (var i = 0; i < count; i++)
            {
                var sc = sources[i];
                rawComponents[i] = new RawComponentValue(sc.ComponentType.Value, sc.RuntimeType, sc.ComponentType, _slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
            }
            return (archetype, rawComponents, count);
        }
        finally
        {
            ReturnExtracted(types, sources);
        }
    }

    private (Signature Signature, RawComponentValue[] Components) BuildCreatedEntityComponentsForDelta(in CreatedState state)
    {
        var (types, sources, count) = ExtractAndSortComponents(in state);
        try
        {
            var rawComponents = new RawComponentValue[count];
            var signatureComponents = new ComponentType[count];
            for (var i = 0; i < count; i++)
            {
                var sc = sources[i];
                rawComponents[i] = new RawComponentValue(sc.ComponentType.Value, sc.RuntimeType, sc.ComponentType, _slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
                signatureComponents[i] = sc.ComponentType;
            }
            return (Signature.CreateNormalized(signatureComponents), rawComponents);
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
            else if (state.Map.Count == 0 && state.Map.Overflow is null)
            {
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, Signature.Empty, Array.Empty<RawComponentValue>()));
            }
            else
            {
                var (signature, components) = BuildCreatedEntityComponentsForDelta(in state);
                delta.CreatedEntities.Add(new RawCreatedEntity(entity, signature, components));
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

            if (existingOps.Count >= 1) EmitOp(in existingOps.Value0, entity, delta);
            if (existingOps.Count >= 2) EmitOp(in existingOps.Value1, entity, delta);
            if (existingOps.Count >= 3) EmitOp(in existingOps.Value2, entity, delta);
            if (existingOps.Count >= 4) EmitOp(in existingOps.Value3, entity, delta);
            if (existingOps.Overflow is not null)
            {
                foreach (var kv in existingOps.Overflow)
                {
                    var overflowSlot = kv.Value;
                    EmitOp(in overflowSlot, entity, delta);
                }
            }
        }

        for (var i = 0; i < _existingDestroyCount; i++)
        {
            delta.DestroyedEntities.Add(_existingDestroyEntities[i]);
        }
    }

    private void Clear()
    {
        _cloneCommands.Clear();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void CopyComponentFromChunk(Chunk chunk, int columnIndex, int row, int size, out int slabIndex, out int offset)
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
            chunk.ReadComponentRaw(columnIndex, row, ptr);
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

        var size = runtimeType == typeof(object) ? 0 : ComponentSizeCache.GetSize(runtimeType);
        var writer = runtimeType == typeof(object) ? null! : ComponentWriterCache.GetColumnWriter(runtimeType);
        info = (runtimeType, componentType, size, writer);
        _typeInfoCache.Add(componentTypeId, info);
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
        public InlineMap<int, CreatedComponent> Map;
        public bool Destroyed;
    }

    private readonly record struct CreatedComponent(Type RuntimeType, ComponentType ComponentType, int SlabIndex, int DataOffset, int DataSize);

    private sealed class FrozenBufferState
    {
        public CreatedState[] CreatedStatePool = null!;
        public int CreatedStatePoolCount;
        public Entity[] CreatedEntityByPoolIndex = null!;
        public int[] CreatedStateLookup = null!;
        public int MaxCreatedEntityId;

        public InlineMap<int, EntityOpSlot>[] OpsPool = null!;
        public int OpsPoolCount;
        public Entity[] OpsEntityByPoolIndex = null!;
        public int[] OpsLookup = null!;
        public int MaxOpsEntityId;

        public Entity[] ExistingDestroyEntities = null!;
        public int ExistingDestroyCount;
        public Dictionary<Entity, HierarchyIntent> HierarchyByChild = null!;
        public List<byte[]> Slabs = null!;
        public bool HasCreatedEntities;
        public Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> TypeInfoCache = null!;
    }
}
