using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiniArch.Core;

/// <summary>
/// Single-threaded command buffer that deduplicates during recording,
/// eliminating the compile step. Best for single-threaded use where
/// total record+submit time matters more than raw recording speed.
/// </summary>
public sealed class FastCommandBuffer : ICommandRecorder
{
    private const int DefaultSlabSize = 4096;

    private readonly World _world;
    private readonly CommandBufferEntityAllocator _allocator;
    private readonly Dictionary<long, AddSetEntry> _adds = new();
    private readonly Dictionary<long, AddSetEntry> _sets = new();
    private readonly Dictionary<long, RemoveEntry> _removes = new();
    private Entity[] _opsEntityLookup = Array.Empty<Entity>();
    private int _maxOpsEntityId;
    private readonly HashSet<Entity> _existingDestroys = new();
    private CreatedState[] _createdStatePool = Array.Empty<CreatedState>();
    private Entity[] _createdEntityByPoolIndex = Array.Empty<Entity>();
    private int _createdStatePoolCount;
    private int[] _createdStateLookup = Array.Empty<int>();
    private int _maxCreatedEntityId;
    private readonly Dictionary<Entity, HierarchyIntent> _hierarchyByChild = new();
    private readonly Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size, ComponentWriterCache.ColumnWriterDelegate Writer)> _typeInfoCache = new();
    private readonly List<byte[]> _slabs = new();
    private readonly FrameDelta _reusableDelta = new();
    private readonly List<(int ComponentTypeId, CreatedComponent Component)> _tempComponents = new();
    private bool _hasCreatedEntities;
    private int _currentSlabIndex = -1;
    private int _currentSlabOffset;

    /// <summary>
    /// Creates a buffer for a world.
    /// </summary>
    public FastCommandBuffer(World world)
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

    /// <summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCreatedStateIndex(Entity entity)
    {
        var id = entity.Id;
        return (uint)id < (uint)_createdStateLookup.Length ? _createdStateLookup[id] : -1;
    }

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

        var key = MakeKey(entity.Id, componentTypeId);
        EnsureOpsEntityLookup(entity.Id);
        _opsEntityLookup[entity.Id] = entity;
        _removes.Remove(key);
        _adds[key] = new AddSetEntry(info.ComponentType, info.RuntimeType, slabIndex, offset, info.Size, info.Writer);
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

        var key = MakeKey(entity.Id, componentTypeId);
        EnsureOpsEntityLookup(entity.Id);
        _opsEntityLookup[entity.Id] = entity;
        _sets[key] = new AddSetEntry(info.ComponentType, info.RuntimeType, slabIndex, offset, info.Size, info.Writer);
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

        var key = MakeKey(entity.Id, componentTypeId);
        EnsureOpsEntityLookup(entity.Id);
        _opsEntityLookup[entity.Id] = entity;
        _adds.Remove(key);
        _sets.Remove(key);
        var info = ResolveTypeInfo(componentTypeId);
        _removes[key] = new RemoveEntry(info.ComponentType);
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
        if (_adds.Count == 0 && _sets.Count == 0 && _removes.Count == 0 &&
            _existingDestroys.Count == 0 && _createdStatePoolCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return false;
        }

        BuildDelta(_reusableDelta);
        if (_reusableDelta.IsEmpty)
        {
            Clear();
            return false;
        }

        _world.ReplayTrusted(_reusableDelta);
        _reusableDelta.Clear();
        Clear();
        return true;
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

        delta.ReleasedEntities.EnsureCapacity(releasedCount);
        delta.CreatedEntities.EnsureCapacity(createdCount);
        delta.LinkCommands.EnsureCapacity(_hierarchyByChild.Count);
        delta.UnlinkCommands.EnsureCapacity(_hierarchyByChild.Count);
        delta.AddCommands.EnsureCapacity(_adds.Count);
        delta.SetCommands.EnsureCapacity(_sets.Count);
        delta.RemoveCommands.EnsureCapacity(_removes.Count);
        delta.DestroyedEntities.EnsureCapacity(_existingDestroys.Count);

        for (var poolIdx = 0; poolIdx < _createdStatePoolCount; poolIdx++)
        {
            ref readonly var state = ref _createdStatePool[poolIdx];
            var entity = _createdEntityByPoolIndex[poolIdx];
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

                    delta.CreatedEntities.Add(new RawCreatedEntity(entity, Signature.CreateNormalized(signatureComponents), rawComponents));
                }
                finally
                {
                    ArrayPool<CreatedComponent>.Shared.Return(sourceComponents);
                    ArrayPool<ComponentType>.Shared.Return(components);
                }
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

        foreach (var (key, entry) in _adds)
        {
            var entityId = (int)(key >> 32);
            var componentTypeId = (int)(key & 0xFFFFFFFF);
            var entity = _opsEntityLookup[entityId];
            delta.AddCommands.Add(new RawComponentCommand(entity, componentTypeId, entry.RuntimeType, entry.ComponentType, entry.DataOffset, entry.DataSize, entry.Writer, _slabs[entry.SlabIndex]));
        }

        foreach (var (key, entry) in _sets)
        {
            var entityId = (int)(key >> 32);
            var componentTypeId = (int)(key & 0xFFFFFFFF);
            var entity = _opsEntityLookup[entityId];
            delta.SetCommands.Add(new RawComponentCommand(entity, componentTypeId, entry.RuntimeType, entry.ComponentType, entry.DataOffset, entry.DataSize, entry.Writer, _slabs[entry.SlabIndex]));
        }

        foreach (var (key, entry) in _removes)
        {
            var entityId = (int)(key >> 32);
            var componentTypeId = (int)(key & 0xFFFFFFFF);
            var entity = _opsEntityLookup[entityId];
            delta.RemoveCommands.Add(new RawRemoveCommand(entity, componentTypeId, entry.RuntimeType, entry.ComponentType));
        }

        foreach (var entity in _existingDestroys)
        {
            delta.DestroyedEntities.Add(entity);
        }
    }

    private void Clear()
    {
        _adds.Clear();
        _sets.Clear();
        _removes.Clear();
        _existingDestroys.Clear();
        if (_maxOpsEntityId > 0 && _opsEntityLookup.Length > 0)
        {
            Array.Clear(_opsEntityLookup, 0, _maxOpsEntityId);
        }
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
        var writer = runtimeType == typeof(object) ? null : ComponentWriterCache.GetColumnWriter(runtimeType);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long MakeKey(int entityId, int componentTypeId)
    {
        return ((long)entityId << 32) | (uint)componentTypeId;
    }

    private void EnsureOpsEntityLookup(int entityId)
    {
        if (entityId < _opsEntityLookup.Length)
        {
            if (entityId >= _maxOpsEntityId) _maxOpsEntityId = entityId + 1;
            return;
        }
        var newLen = _opsEntityLookup.Length == 0 ? 64 : _opsEntityLookup.Length;
        while (newLen <= entityId) newLen *= 2;
        var newArr = new Entity[newLen];
        if (_opsEntityLookup.Length > 0)
            Array.Copy(_opsEntityLookup, newArr, _opsEntityLookup.Length);
        _opsEntityLookup = newArr;
        if (entityId >= _maxOpsEntityId) _maxOpsEntityId = entityId + 1;
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

    private struct RemoveEntry
    {
        public ComponentType ComponentType;
        public Type RuntimeType;

        public RemoveEntry(ComponentType componentType)
        {
            ComponentType = componentType;
            RuntimeType = typeof(object);
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
}
