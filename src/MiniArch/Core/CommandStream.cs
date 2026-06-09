using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands as an append-only expert-mode stream.
/// </summary>
public sealed class CommandStream
{
    private readonly World _world;
    private StreamEntry[] _entries = [];
    private int _entryCount;
    private int _componentCommandCount;
    private ComponentCommandStore?[] _componentStores = [];
    private Entity[] _createdEntities = [];
    private int _createdCount;
    private int[] _createdComponentCounts = [];
    private CreatedComponentRef[] _createdComponents = [];
    private int _createdComponentCount;
    private int[] _createdLookup = [];
    private int _maxCreatedEntityId;
    private Entity _lastCreatedEntity;
    private int _lastCreatedIndex = -1;
    private const int ArchetypeCacheSize = 4;
    private int _archetypeCacheCount;
    private int _archetypeCacheGeneration = -1;
    private ArchetypeCacheEntry[] _archetypeCache = [];

    public CommandStream(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public Entity Create()
    {
        var entity = _world.ReserveDeferredEntity();
        RegisterCreatedEntity(entity);
        return entity;
    }

    public void Add<T>(Entity entity, T component)
    {
        var store = GetOrCreateComponentStore<T>();
        var dataIndex = store.Append(component);
        if (_createdCount > 0)
        {
            var createdIndex = GetCreatedIndex(entity);
            if (createdIndex >= 0)
            {
                AppendCreatedComponent(createdIndex, CommandTypeInfo<T>.Type, store, dataIndex, CommandTypeInfo<T>.Size);
                return;
            }
        }

        store.AppendExisting(StreamOpKind.Add, entity, dataIndex);
        _componentCommandCount++;
    }

    public void Set<T>(Entity entity, T component)
    {
        var store = GetOrCreateComponentStore<T>();
        var dataIndex = store.Append(component);
        if (_createdCount > 0)
        {
            var createdIndex = GetCreatedIndex(entity);
            if (createdIndex >= 0)
            {
                AppendCreatedComponent(createdIndex, CommandTypeInfo<T>.Type, store, dataIndex, CommandTypeInfo<T>.Size);
                return;
            }
        }

        store.AppendExisting(StreamOpKind.Set, entity, dataIndex);
        _componentCommandCount++;
    }

    public void Remove<T>(Entity entity)
    {
        Append(new StreamEntry(StreamOpKind.Remove, entity, CommandTypeInfo<T>.Type, -1, 0, 0));
    }

    public void Destroy(Entity entity)
    {
        Append(new StreamEntry(StreamOpKind.Destroy, entity, default, -1, 0, 0));
    }

    public bool Submit()
    {
        if (_entryCount == 0 && _componentCommandCount == 0 && _createdCount == 0)
        {
            return false;
        }

        var createdDestroyed = BuildCreatedDestroyedScratch();
        try
        {
            MaterializeCreatedEntities(createdDestroyed);
            ApplyExistingEntityCommands();
        }
        finally
        {
            if (createdDestroyed.Length > 0)
            {
                ArrayPool<bool>.Shared.Return(createdDestroyed);
            }

            Clear();
        }

        return true;
    }

    public FrameDelta Snapshot()
    {
        var delta = new FrameDelta();
        BuildDelta(delta);
        delta.DeepCopyOwnedData();
        return delta;
    }

    private void RegisterCreatedEntity(Entity entity)
    {
        if (_createdCount == _createdEntities.Length)
        {
            var newSize = _createdEntities.Length == 0 ? 64 : _createdEntities.Length * 2;
            Array.Resize(ref _createdEntities, newSize);
            Array.Resize(ref _createdComponentCounts, newSize);
        }

        if (entity.Id >= _createdLookup.Length)
        {
            var newLen = _createdLookup.Length == 0 ? 64 : _createdLookup.Length;
            while (newLen <= entity.Id) newLen *= 2;
            var next = new int[newLen];
            Array.Fill(next, -1);
            if (_createdLookup.Length > 0)
            {
                Array.Copy(_createdLookup, next, _createdLookup.Length);
            }

            _createdLookup = next;
        }

        _createdEntities[_createdCount] = entity;
        _createdComponentCounts[_createdCount] = 0;
        _createdLookup[entity.Id] = _createdCount;
        _lastCreatedEntity = entity;
        _lastCreatedIndex = _createdCount;
        _createdCount++;
        if (entity.Id >= _maxCreatedEntityId) _maxCreatedEntityId = entity.Id + 1;
    }

    private void AppendCreatedComponent(int createdIndex, ComponentType componentType, ComponentCommandStore store, int dataIndex, int size)
    {
        if (_createdComponentCount == _createdComponents.Length)
        {
            Array.Resize(ref _createdComponents, _createdComponents.Length == 0 ? 256 : _createdComponents.Length * 2);
        }

        _createdComponents[_createdComponentCount++] = new CreatedComponentRef(createdIndex, componentType, store, dataIndex, size);
        _createdComponentCounts[createdIndex]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetCreatedIndex(Entity entity)
    {
        if (entity == _lastCreatedEntity)
        {
            return _lastCreatedIndex;
        }

        var id = entity.Id;
        if ((uint)id >= (uint)_createdLookup.Length)
        {
            return -1;
        }

        var index = _createdLookup[id];
        return (uint)index < (uint)_createdCount && _createdEntities[index] == entity ? index : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCreatedEntity(Entity entity) => GetCreatedIndex(entity) >= 0;

    private void Append(StreamEntry entry)
    {
        if (_entryCount == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length == 0 ? 256 : _entries.Length * 2);
        }

        _entries[_entryCount++] = entry;
    }

    private ComponentCommandStore<T> GetOrCreateComponentStore<T>()
    {
        var componentTypeId = CommandTypeInfo<T>.Type.Value;
        if (componentTypeId >= _componentStores.Length)
        {
            Array.Resize(ref _componentStores, componentTypeId + 1);
        }

        var store = _componentStores[componentTypeId];
        if (store == null)
        {
            store = new ComponentCommandStore<T>();
            _componentStores[componentTypeId] = store;
        }

        return (ComponentCommandStore<T>)store;
    }

    private bool[] BuildCreatedDestroyedScratch()
    {
        if (_createdCount == 0)
        {
            return [];
        }

        var destroyed = ArrayPool<bool>.Shared.Rent(_createdCount);
        Array.Clear(destroyed, 0, _createdCount);
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.Kind != StreamOpKind.Destroy)
            {
                continue;
            }

            var createdIndex = GetCreatedIndex(entry.Entity);
            if (createdIndex >= 0)
            {
                destroyed[createdIndex] = true;
            }
        }

        return destroyed;
    }

    private void MaterializeCreatedEntities(bool[] createdDestroyed)
    {
        var offsets = BuildCreatedComponentOffsets(out var totalComponentCount);
        var writes = totalComponentCount == 0 ? [] : ArrayPool<int>.Shared.Rent(_createdCount);
        var values = totalComponentCount == 0 ? [] : ArrayPool<CreatedComponentRef>.Shared.Rent(totalComponentCount);
        try
        {
            if (totalComponentCount > 0)
            {
                Array.Copy(offsets, writes, _createdCount);
                for (var i = 0; i < _createdComponentCount; i++)
                {
                    ref readonly var component = ref _createdComponents[i];
                    values[writes[component.CreatedIndex]++] = component;
                }
            }

            for (var i = 0; i < _createdCount; i++)
            {
                var entity = _createdEntities[i];
                if (createdDestroyed.Length > 0 && createdDestroyed[i])
                {
                    _world.ReleaseReservedEntity(entity);
                    continue;
                }

                var count = _createdComponentCounts[i];
                if (count == 0)
                {
                    _world.MaterializeReservedEntity(entity, Array.Empty<RawComponentValue>(), reservationChecked: true);
                    continue;
                }

                MaterializeCreatedEntity(entity, values.AsSpan(offsets[i], count));
            }
        }
        finally
        {
            if (values.Length > 0) ArrayPool<CreatedComponentRef>.Shared.Return(values);
            if (writes.Length > 0) ArrayPool<int>.Shared.Return(writes);
            if (offsets.Length > 0) ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private int[] BuildCreatedComponentOffsets(out int totalComponentCount)
    {
        totalComponentCount = 0;
        if (_createdCount == 0)
        {
            return [];
        }

        var offsets = ArrayPool<int>.Shared.Rent(_createdCount + 1);
        offsets[0] = 0;
        for (var i = 0; i < _createdCount; i++)
        {
            totalComponentCount += _createdComponentCounts[i];
            offsets[i + 1] = totalComponentCount;
        }

        return offsets;
    }

    private void MaterializeCreatedEntity(Entity entity, ReadOnlySpan<CreatedComponentRef> components)
    {
        if (TryMaterializeNormalizedCreatedEntity(entity, components))
        {
            return;
        }

        var count = components.Length;
        var componentTypes = ArrayPool<ComponentType>.Shared.Rent(count);
        var sortedComponents = ArrayPool<CreatedComponentRef>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                componentTypes[i] = components[i].ComponentType;
                sortedComponents[i] = components[i];
            }

            Array.Sort(componentTypes, sortedComponents, 0, count);
            var archetype = LookupArchetypeCache(componentTypes, count, out var hash);
            if (archetype == null)
            {
                var signatureTypes = new ComponentType[count];
                Array.Copy(componentTypes, signatureTypes, count);
                archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(signatureTypes));
                InsertArchetypeCache(count, archetype, hash);
            }

            _world.MaterializeReservedEntityTyped(entity, archetype, sortedComponents.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<ComponentType>.Shared.Return(componentTypes);
            ArrayPool<CreatedComponentRef>.Shared.Return(sortedComponents);
        }
    }

    private bool TryMaterializeNormalizedCreatedEntity(Entity entity, ReadOnlySpan<CreatedComponentRef> components)
    {
        if (!IsStrictlySortedByComponentType(components))
        {
            return false;
        }

        var archetype = LookupArchetypeCache(components, out var hash);
        if (archetype == null)
        {
            var signatureTypes = new ComponentType[components.Length];
            for (var i = 0; i < components.Length; i++)
            {
                signatureTypes[i] = components[i].ComponentType;
            }

            archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(signatureTypes));
            InsertArchetypeCache(components.Length, archetype, hash);
        }

        _world.MaterializeReservedEntityTyped(entity, archetype, components);
        return true;
    }

    private static bool IsStrictlySortedByComponentType(ReadOnlySpan<CreatedComponentRef> components)
    {
        for (var i = 1; i < components.Length; i++)
        {
            if (components[i - 1].ComponentType.Value >= components[i].ComponentType.Value)
            {
                return false;
            }
        }

        return true;
    }

    private Archetype? LookupArchetypeCache(ComponentType[] componentTypes, int count, out int hash)
    {
        if (_archetypeCacheGeneration != _world.CreateArchetypeCacheGeneration)
        {
            _archetypeCacheGeneration = _world.CreateArchetypeCacheGeneration;
            _archetypeCacheCount = 0;
        }

        hash = ComputeComponentHash(componentTypes, count);
        for (var i = 0; i < _archetypeCacheCount; i++)
        {
            var entry = _archetypeCache[i];
            if (entry.Hash == hash && entry.ComponentCount == count && entry.Archetype.Signature.AsSpan().SequenceEqual(componentTypes.AsSpan(0, count)))
            {
                return entry.Archetype;
            }
        }

        return null;
    }

    private Archetype? LookupArchetypeCache(ReadOnlySpan<CreatedComponentRef> components, out int hash)
    {
        if (_archetypeCacheGeneration != _world.CreateArchetypeCacheGeneration)
        {
            _archetypeCacheGeneration = _world.CreateArchetypeCacheGeneration;
            _archetypeCacheCount = 0;
        }

        hash = ComputeComponentHash(components);
        for (var i = 0; i < _archetypeCacheCount; i++)
        {
            var entry = _archetypeCache[i];
            if (entry.Hash == hash && entry.ComponentCount == components.Length && SignatureEquals(entry.Archetype.Signature.AsSpan(), components))
            {
                return entry.Archetype;
            }
        }

        return null;
    }

    private static bool SignatureEquals(ReadOnlySpan<ComponentType> signature, ReadOnlySpan<CreatedComponentRef> components)
    {
        for (var i = 0; i < components.Length; i++)
        {
            if (signature[i] != components[i].ComponentType)
            {
                return false;
            }
        }

        return true;
    }

    private void InsertArchetypeCache(int count, Archetype archetype, int hash)
    {
        if (_archetypeCache.Length == 0)
        {
            _archetypeCache = new ArchetypeCacheEntry[ArchetypeCacheSize];
        }

        var index = _archetypeCacheCount < ArchetypeCacheSize
            ? _archetypeCacheCount++
            : hash & (ArchetypeCacheSize - 1);
        _archetypeCache[index] = new ArchetypeCacheEntry(hash, count, archetype);
    }

    private static int ComputeComponentHash(ComponentType[] componentTypes, int count)
    {
        var hash = new HashCode();
        for (var i = 0; i < count; i++)
        {
            hash.Add(componentTypes[i].Value);
        }

        return hash.ToHashCode();
    }

    private static int ComputeComponentHash(ReadOnlySpan<CreatedComponentRef> components)
    {
        var hash = new HashCode();
        for (var i = 0; i < components.Length; i++)
        {
            hash.Add(components[i].ComponentType.Value);
        }

        return hash.ToHashCode();
    }

    private void ApplyExistingEntityCommands()
    {
        for (var i = 0; i < _componentStores.Length; i++)
        {
            _componentStores[i]?.ApplyExistingCommands(_world);
        }

        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (IsCreatedEntity(entry.Entity))
            {
                continue;
            }

            switch (entry.Kind)
            {
                case StreamOpKind.Remove:
                    _world.RemoveBoxed(entry.Entity, entry.ComponentType);
                    break;
                case StreamOpKind.Destroy:
                    if (_world.IsAlive(entry.Entity))
                    {
                        _world.Destroy(entry.Entity);
                    }
                    break;
            }
        }
    }

    private void BuildDelta(FrameDelta delta)
    {
        var createdDestroyed = BuildCreatedDestroyedScratch();
        try
        {
            delta.ReservedEntities.EnsureCapacity(_createdCount);
            delta.CreatedEntities.EnsureCapacity(_createdCount);
            delta.AddCommands.EnsureCapacity(_componentCommandCount);
            delta.SetCommands.EnsureCapacity(_componentCommandCount);
            delta.DestroyedEntities.EnsureCapacity(_entryCount);

            for (var i = 0; i < _createdCount; i++)
            {
                var entity = _createdEntities[i];
                delta.ReservedEntities.Add(entity);
                if (createdDestroyed.Length > 0 && createdDestroyed[i])
                {
                    delta.ReleasedEntities.Add(entity);
                    continue;
                }

                delta.CreatedEntities.Add(new RawCreatedEntity(entity, BuildCreatedComponentsForDelta(i)));
            }

            for (var i = 0; i < _componentStores.Length; i++)
            {
                _componentStores[i]?.EmitDelta(delta, (ComponentType)i);
            }

            for (var i = 0; i < _entryCount; i++)
            {
                ref readonly var entry = ref _entries[i];
                if (IsCreatedEntity(entry.Entity))
                {
                    continue;
                }

                switch (entry.Kind)
                {
                    case StreamOpKind.Remove:
                        delta.RemoveCommands.Add(new RawRemoveCommand(entry.Entity, entry.ComponentType));
                        break;
                    case StreamOpKind.Destroy:
                        delta.DestroyedEntities.Add(entry.Entity);
                        break;
                }
            }
        }
        finally
        {
            if (createdDestroyed.Length > 0)
            {
                ArrayPool<bool>.Shared.Return(createdDestroyed);
            }
        }
    }

    private RawComponentValue[] BuildCreatedComponentsForDelta(int createdIndex)
    {
        var count = _createdComponentCounts[createdIndex];
        if (count == 0)
        {
            return [];
        }

        var components = new RawComponentValue[count];
        var write = 0;
        for (var i = 0; i < _createdComponentCount; i++)
        {
            ref readonly var component = ref _createdComponents[i];
            if (component.CreatedIndex == createdIndex)
            {
                components[write++] = ToRawComponentValue(component);
            }
        }

        return components;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RawComponentValue ToRawComponentValue(in CreatedComponentRef component)
    {
        var data = new byte[component.DataSize];
        component.Store.WriteRaw(component.DataIndex, data);
        return new RawComponentValue(component.ComponentType, data, 0, component.DataSize);
    }

    private void Clear()
    {
        _entryCount = 0;
        _componentCommandCount = 0;
        for (var i = 0; i < _componentStores.Length; i++)
        {
            _componentStores[i]?.Clear();
        }

        for (var i = 0; i < _maxCreatedEntityId; i++)
        {
            if (i < _createdLookup.Length)
            {
                _createdLookup[i] = -1;
            }
        }

        _createdCount = 0;
        _createdComponentCount = 0;
        _maxCreatedEntityId = 0;
        _lastCreatedEntity = default;
        _lastCreatedIndex = -1;

    }

    private static class CommandTypeInfo<T>
    {
        public static readonly ComponentType Type = Component<T>.ComponentType;
        public static readonly int Size = Unsafe.SizeOf<T>();
    }

    private readonly record struct StreamEntry(
        StreamOpKind Kind,
        Entity Entity,
        ComponentType ComponentType,
        int SlabIndex,
        int DataOffset,
        int DataSize);

    internal readonly record struct CreatedComponentRef(
        int CreatedIndex,
        ComponentType ComponentType,
        ComponentCommandStore Store,
        int DataIndex,
        int DataSize);

    private readonly record struct ArchetypeCacheEntry(int Hash, int ComponentCount, Archetype Archetype);

    internal abstract class ComponentCommandStore
    {
        public abstract void ApplyExistingCommands(World world);
        public abstract void EmitDelta(FrameDelta delta, ComponentType componentType);
        public abstract void WriteToArchetype(Archetype archetype, int rowIndex, ComponentType componentType, int index);
        public abstract void WriteRaw(int index, byte[] destination);
        public abstract void Clear();
    }

    private sealed class ComponentCommandStore<T> : ComponentCommandStore
    {
        private T[] _components = [];
        private int _count;
        private ExistingComponentCommand[] _existingCommands = [];
        private int _existingCount;

        public int Append(T component)
        {
            if (_count == _components.Length)
            {
                Array.Resize(ref _components, _components.Length == 0 ? 256 : _components.Length * 2);
            }

            var index = _count++;
            _components[index] = component;
            return index;
        }

        public void AppendExisting(StreamOpKind kind, Entity entity, int dataIndex)
        {
            if (_existingCount == _existingCommands.Length)
            {
                Array.Resize(ref _existingCommands, _existingCommands.Length == 0 ? 256 : _existingCommands.Length * 2);
            }

            _existingCommands[_existingCount++] = new ExistingComponentCommand(kind, entity, dataIndex);
        }

        public override void ApplyExistingCommands(World world)
        {
            for (var i = 0; i < _existingCount; i++)
            {
                ref readonly var command = ref _existingCommands[i];
                if (command.Kind == StreamOpKind.Add)
                {
                    world.Add(command.Entity, _components[command.DataIndex]);
                }
                else
                {
                    world.Set(command.Entity, _components[command.DataIndex]);
                }
            }
        }

        public override void EmitDelta(FrameDelta delta, ComponentType componentType)
        {
            var size = Unsafe.SizeOf<T>();
            for (var i = 0; i < _existingCount; i++)
            {
                ref readonly var command = ref _existingCommands[i];
                var data = new byte[size];
                WriteRaw(command.DataIndex, data);
                var raw = new RawComponentCommand(command.Entity, componentType, 0, size, data);
                if (command.Kind == StreamOpKind.Add)
                {
                    delta.AddCommands.Add(raw);
                }
                else
                {
                    delta.SetCommands.Add(raw);
                }
            }
        }

        public override void WriteRaw(int index, byte[] destination)
        {
            Unsafe.WriteUnaligned(ref destination[0], _components[index]);
        }

        public override void WriteToArchetype(Archetype archetype, int rowIndex, ComponentType componentType, int index)
        {
            archetype.SetComponentAtTyped(archetype.GetComponentIndex(componentType), rowIndex, in _components[index]);
        }

        public override void Clear()
        {
            _count = 0;
            _existingCount = 0;
        }
    }

    private readonly record struct ExistingComponentCommand(StreamOpKind Kind, Entity Entity, int DataIndex);

    private enum StreamOpKind : byte
    {
        Add,
        Set,
        Remove,
        Destroy,
    }
}
