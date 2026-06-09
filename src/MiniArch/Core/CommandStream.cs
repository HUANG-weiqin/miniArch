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
    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);
    private Dictionary<Entity, HierarchyIntent> _hierarchyByChild = new();
    private const int ArchetypeCacheSize = 4;
    private int _archetypeCacheCount;
    private int _archetypeCacheGeneration = -1;
    private ArchetypeCacheEntry[] _archetypeCache = [];
    private Archetype? _lastCreatedArchetype;
    private int _lastCreatedComponentCount;
    private ComponentType _lastCreatedFirstComponentType;

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
        if (_createdCount > 0)
        {
            var createdIndex = GetCreatedIndex(entity);
            if (createdIndex >= 0)
            {
                var dataIndex = store.Append(component);
                AppendCreatedComponent(createdIndex, CommandTypeInfo<T>.Type, store, dataIndex, CommandTypeInfo<T>.Size);
                return;
            }
        }

        store.AddExisting(StreamOpKind.Add, entity, in component);
        _componentCommandCount++;
    }

    public void Set<T>(Entity entity, T component)
    {
        var store = GetOrCreateComponentStore<T>();
        if (_createdCount > 0)
        {
            var createdIndex = GetCreatedIndex(entity);
            if (createdIndex >= 0)
            {
                var dataIndex = store.Append(component);
                AppendCreatedComponent(createdIndex, CommandTypeInfo<T>.Type, store, dataIndex, CommandTypeInfo<T>.Size);
                return;
            }
        }

        store.AddExisting(StreamOpKind.Set, entity, in component);
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
    /// Clones an existing entity with all its components and children hierarchy.
    /// </summary>
    public Entity Clone(Entity source)
    {
        if (!_world.TryGetLocation(source, out var location))
        {
            throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");
        }

        var deferred = Create();
        SnapshotEntityToCreated(deferred, location);
        CloneChildrenRecursive(source, deferred);
        return deferred;
    }

    private void SnapshotEntityToCreated(Entity deferred, EntityInfo location)
    {
        var createdIndex = GetCreatedIndex(deferred);
        if (createdIndex < 0) return;

        var archetype = location.Archetype;
        var sourceRow = location.RowIndex;
        var components = archetype.Signature.AsSpan();

        for (var i = 0; i < components.Length; i++)
        {
            var componentType = components[i];
            var store = GetOrCreateComponentStore(componentType);
            var dataIndex = store.AppendFromArchetype(archetype, i, sourceRow);
            var size = ComponentSizeCache.GetSize(ComponentRegistry.Shared.GetType(componentType));
            AppendCreatedComponent(createdIndex, componentType, store, dataIndex, size);
        }
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
                    Array.Resize(ref stack, stack.Length * 2);
                    Array.Resize(ref cloneStack, cloneStack.Length * 2);
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
                SnapshotEntityToCreated(cloneChild, childLocation);
                Link(cloneParent, cloneChild);

                foreach (var grandChild in _world.Hierarchy.EnumerateChildren(_world, srcChild))
                {
                    if (stackCount >= stack.Length)
                    {
                        Array.Resize(ref stack, stack.Length * 2);
                        Array.Resize(ref cloneStack, cloneStack.Length * 2);
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

    public bool Submit()
    {
        if (_entryCount == 0 && _componentCommandCount == 0 && _createdCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return false;
        }

        var createdDestroyed = BuildCreatedDestroyedScratch();
        try
        {
            MaterializeCreatedEntities(createdDestroyed);
            ApplyExistingEntityCommands();
            ApplyHierarchy(createdDestroyed);
            ApplyDestroyEntries();
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

    /// <summary>
    /// Swaps out internal state, submits to world on the calling thread,
    /// and builds a self-contained FrameDelta on a background thread.
    /// </summary>
    public Task<FrameDelta> SubmitAndSnapshotAsync()
    {
        if (_entryCount == 0 && _componentCommandCount == 0 && _createdCount == 0 &&
            _hierarchyByChild.Count == 0)
        {
            return Task.FromResult(new FrameDelta());
        }

        var frozen = SwapOutState();
        var task = Task.Run(() => BuildFromFrozen(frozen));

        SubmitFromFrozen(frozen);

        return task.ContinueWith(t =>
        {
            frozen.HierarchyByChild.Clear();
            return t.Result.Delta;
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private FrozenStreamState SwapOutState()
    {
        var frozen = new FrozenStreamState
        {
            Entries = _entries,
            EntryCount = _entryCount,
            ComponentStores = _componentStores,
            ComponentCommandCount = _componentCommandCount,
            CreatedEntities = _createdEntities,
            CreatedCount = _createdCount,
            CreatedComponentCounts = _createdComponentCounts,
            CreatedComponents = _createdComponents,
            CreatedComponentCount = _createdComponentCount,
            CreatedLookup = _createdLookup,
            MaxCreatedEntityId = _maxCreatedEntityId,
            HierarchyByChild = _hierarchyByChild,
        };

        // Reset self to empty state
        _entries = [];
        _entryCount = 0;
        _componentStores = [];
        _componentCommandCount = 0;
        _createdEntities = [];
        _createdCount = 0;
        _createdComponentCounts = [];
        _createdComponents = [];
        _createdComponentCount = 0;
        _createdLookup = [];
        _maxCreatedEntityId = 0;
        _lastCreatedEntity = default;
        _lastCreatedIndex = -1;
        _hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        _archetypeCacheCount = 0;

        return frozen;
    }

    private void SubmitFromFrozen(FrozenStreamState frozen)
    {
        var createdDestroyed = BuildCreatedDestroyedScratch(frozen);
        try
        {
            MaterializeCreatedEntities(frozen, createdDestroyed);
            ApplyExistingEntityCommands(frozen);
            ApplyHierarchy(frozen, createdDestroyed);
            ApplyDestroyEntries(frozen);
        }
        finally
        {
            if (createdDestroyed.Length > 0)
            {
                ArrayPool<bool>.Shared.Return(createdDestroyed);
            }
        }
    }

    private static (FrameDelta Delta, int CopiedBytes) BuildFromFrozen(FrozenStreamState frozen)
    {
        var delta = new FrameDelta();

        var createdDestroyed = BuildCreatedDestroyedScratch(frozen);
        try
        {
            delta.ReservedEntities.EnsureCapacity(frozen.CreatedCount);
            delta.CreatedEntities.EnsureCapacity(frozen.CreatedCount);
            delta.AddCommands.EnsureCapacity(frozen.ComponentCommandCount);
            delta.SetCommands.EnsureCapacity(frozen.ComponentCommandCount);
            delta.DestroyedEntities.EnsureCapacity(frozen.EntryCount);

            for (var i = 0; i < frozen.CreatedCount; i++)
            {
                var entity = frozen.CreatedEntities[i];
                delta.ReservedEntities.Add(entity);
                if (createdDestroyed.Length > 0 && createdDestroyed[i])
                {
                    delta.ReleasedEntities.Add(entity);
                    continue;
                }

                delta.CreatedEntities.Add(new RawCreatedEntity(entity, BuildCreatedComponentsForDelta(frozen, i)));
            }

            for (var i = 0; i < frozen.ComponentStores.Length; i++)
            {
                frozen.ComponentStores[i]?.EmitDelta(delta, (ComponentType)i);
            }

            for (var i = 0; i < frozen.EntryCount; i++)
            {
                ref readonly var entry = ref frozen.Entries[i];
                // Skip entries targeting created entities — their lifecycle is handled above
                if (GetCreatedIndex(frozen, entry.Entity) >= 0)
                {
                    continue;
                }

                if (entry.Kind == StreamOpKind.Remove)
                {
                    delta.RemoveCommands.Add(new RawRemoveCommand(entry.Entity, entry.ComponentType));
                }
                else if (entry.Kind == StreamOpKind.Destroy)
                {
                    delta.DestroyedEntities.Add(entry.Entity);
                }
            }

            foreach (var (child, intent) in frozen.HierarchyByChild)
            {
                // Skip if child is a destroyed created entity
                var childCreatedIdx = GetCreatedIndex(frozen, child);
                if (childCreatedIdx >= 0 && createdDestroyed.Length > 0 && createdDestroyed[childCreatedIdx]) continue;
                // Skip if child is an existing entity marked for destroy
                if (childCreatedIdx < 0 && IsEntityMarkedForDestroy(frozen, child)) continue;

                if (intent.IsLinked)
                {
                    // Skip if parent is a destroyed created entity
                    var parentCreatedIdx = GetCreatedIndex(frozen, intent.Parent);
                    if (parentCreatedIdx >= 0 && createdDestroyed.Length > 0 && createdDestroyed[parentCreatedIdx]) continue;

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
            if (createdDestroyed.Length > 0)
            {
                ArrayPool<bool>.Shared.Return(createdDestroyed);
            }
        }

        var copiedBytes = delta.DeepCopyOwnedData();
        return (delta, copiedBytes);
    }

    private void MaterializeCreatedEntities(FrozenStreamState frozen, bool[] createdDestroyed)
    {
        var offsets = BuildCreatedComponentOffsets(frozen, out var totalComponentCount);
        var writes = totalComponentCount == 0 ? [] : ArrayPool<int>.Shared.Rent(frozen.CreatedCount);
        var values = totalComponentCount == 0 ? [] : ArrayPool<CreatedComponentRef>.Shared.Rent(totalComponentCount);
        try
        {
            if (totalComponentCount > 0)
            {
                Array.Copy(offsets, writes, frozen.CreatedCount);
                for (var i = 0; i < frozen.CreatedComponentCount; i++)
                {
                    ref readonly var component = ref frozen.CreatedComponents[i];
                    values[writes[component.CreatedIndex]++] = component;
                }
            }

            // Group by archetype — same as the non-frozen Submit hot path.
            GroupByArchetypeFrozen(frozen, values, offsets, createdDestroyed);
        }
        finally
        {
            if (values.Length > 0) ArrayPool<CreatedComponentRef>.Shared.Return(values);
            if (writes.Length > 0) ArrayPool<int>.Shared.Return(writes);
            if (offsets.Length > 0) ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private void GroupByArchetypeFrozen(
        FrozenStreamState frozen, CreatedComponentRef[] values, int[] offsets, bool[] createdDestroyed)
    {
        const int maxGroups = 64;
        var groupKeys = ArrayPool<int>.Shared.Rent(maxGroups);
        var groupArchetypes = ArrayPool<Archetype?>.Shared.Rent(maxGroups);
        var entityGroup = ArrayPool<int>.Shared.Rent(frozen.CreatedCount);
        int groupCount = 0;

        for (var i = 0; i < frozen.CreatedCount; i++)
        {
            if (createdDestroyed.Length > 0 && createdDestroyed[i])
            {
                _world.ReleaseReservedEntity(frozen.CreatedEntities[i]);
                entityGroup[i] = -1;
                continue;
            }

            var count = frozen.CreatedComponentCounts[i];
            if (count == 0)
            {
                _world.MaterializeReservedEntity(frozen.CreatedEntities[i], Array.Empty<RawComponentValue>(), reservationChecked: true);
                entityGroup[i] = -1;
                continue;
            }

            var span = values.AsSpan(offsets[i], count);
            var key = ComputeGroupKey(span);

            int g;
            for (g = 0; g < groupCount; g++)
            {
                if (groupKeys[g] == key && ArchetypeMatchesComponentSpan(groupArchetypes[g]!, span))
                    break;
            }

            if (g == groupCount)
            {
                groupKeys[g] = key;
                groupArchetypes[g] = ResolveArchetypeForSpan(span);
                groupCount++;
            }

            entityGroup[i] = g;
        }

        for (var i = 0; i < frozen.CreatedCount; i++)
        {
            var g = entityGroup[i];
            if (g < 0) continue;
            var entity = frozen.CreatedEntities[i];
            var count = frozen.CreatedComponentCounts[i];
            _world.MaterializeReservedEntityTyped(entity, groupArchetypes[g]!, values.AsSpan(offsets[i], count));
        }

        ArrayPool<int>.Shared.Return(groupKeys);
        ArrayPool<Archetype?>.Shared.Return(groupArchetypes);
        ArrayPool<int>.Shared.Return(entityGroup);
    }

    private static int[] BuildCreatedComponentOffsets(FrozenStreamState frozen, out int totalComponentCount)
    {
        totalComponentCount = 0;
        if (frozen.CreatedCount == 0)
        {
            return [];
        }

        var offsets = ArrayPool<int>.Shared.Rent(frozen.CreatedCount + 1);
        offsets[0] = 0;
        for (var i = 0; i < frozen.CreatedCount; i++)
        {
            totalComponentCount += frozen.CreatedComponentCounts[i];
            offsets[i + 1] = totalComponentCount;
        }

        return offsets;
    }

    private static bool[] BuildCreatedDestroyedScratch(FrozenStreamState frozen)
    {
        if (frozen.CreatedCount == 0)
        {
            return [];
        }

        var destroyed = ArrayPool<bool>.Shared.Rent(frozen.CreatedCount);
        Array.Clear(destroyed, 0, frozen.CreatedCount);
        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];
            if (entry.Kind != StreamOpKind.Destroy)
            {
                continue;
            }

            var createdIndex = GetCreatedIndex(frozen, entry.Entity);
            if (createdIndex >= 0)
            {
                destroyed[createdIndex] = true;
            }
        }

        return destroyed;
    }

    private static int GetCreatedIndex(FrozenStreamState frozen, Entity entity)
    {
        var id = entity.Id;
        if ((uint)id >= (uint)frozen.CreatedLookup.Length)
        {
            return -1;
        }

        var index = frozen.CreatedLookup[id];
        return (uint)index < (uint)frozen.CreatedCount && frozen.CreatedEntities[index] == entity ? index : -1;
    }

    private void ApplyExistingEntityCommands(FrozenStreamState frozen)
    {
        for (var i = 0; i < frozen.ComponentStores.Length; i++)
        {
            frozen.ComponentStores[i]?.ApplyExistingCommands(_world);
        }

        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];
            if (entry.Kind != StreamOpKind.Remove)
            {
                continue;
            }

            if (GetCreatedIndex(frozen, entry.Entity) >= 0)
            {
                continue;
            }

            _world.RemoveBoxed(entry.Entity, entry.ComponentType);
        }
    }

    private void ApplyHierarchy(FrozenStreamState frozen, bool[] createdDestroyed)
    {
        if (frozen.HierarchyByChild.Count == 0) return;

        foreach (var (child, intent) in frozen.HierarchyByChild)
        {
            if (createdDestroyed.Length > 0)
            {
                var childCreatedIdx = GetCreatedIndex(frozen, child);
                if (childCreatedIdx >= 0 && createdDestroyed[childCreatedIdx]) continue;
            }

            if (IsEntityMarkedForDestroy(frozen, child)) continue;

            if (intent.IsLinked)
            {
                if (createdDestroyed.Length > 0)
                {
                    var parentCreatedIdx = GetCreatedIndex(frozen, intent.Parent);
                    if (parentCreatedIdx >= 0 && createdDestroyed[parentCreatedIdx]) continue;
                }

                _world.Link(intent.Parent, child);
            }
            else
            {
                _world.Unlink(child);
            }
        }
    }

    private static bool IsEntityMarkedForDestroy(FrozenStreamState frozen, Entity entity)
    {
        for (var i = 0; i < frozen.EntryCount; i++)
        {
            if (frozen.Entries[i].Kind == StreamOpKind.Destroy && frozen.Entries[i].Entity == entity)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyDestroyEntries(FrozenStreamState frozen)
    {
        for (var i = 0; i < frozen.EntryCount; i++)
        {
            ref readonly var entry = ref frozen.Entries[i];
            if (entry.Kind != StreamOpKind.Destroy)
            {
                continue;
            }

            if (GetCreatedIndex(frozen, entry.Entity) >= 0)
            {
                continue;
            }

            if (_world.IsAlive(entry.Entity))
            {
                _world.Destroy(entry.Entity);
            }
        }
    }

    private static RawComponentValue[] BuildCreatedComponentsForDelta(FrozenStreamState frozen, int createdIndex)
    {
        var count = frozen.CreatedComponentCounts[createdIndex];
        if (count == 0)
        {
            return [];
        }

        var components = new RawComponentValue[count];
        var write = 0;
        for (var i = 0; i < frozen.CreatedComponentCount; i++)
        {
            ref readonly var component = ref frozen.CreatedComponents[i];
            if (component.CreatedIndex == createdIndex)
            {
                components[write++] = ToRawComponentValue(component);
            }
        }

        return components;
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

    private ComponentCommandStore GetOrCreateComponentStore(ComponentType componentType)
    {
        var id = componentType.Value;
        if (id >= _componentStores.Length)
        {
            Array.Resize(ref _componentStores, id + 1);
        }

        var store = _componentStores[id];
        if (store == null)
        {
            var runtimeType = ComponentRegistry.Shared.GetType(componentType);
            var typedStoreType = typeof(ComponentCommandStore<>).MakeGenericType(runtimeType);
            store = (ComponentCommandStore)Activator.CreateInstance(typedStoreType)!;
            _componentStores[id] = store;
        }

        return store;
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

            // Group created entities by component combination — archetype lookup once per group.
            // Uses (componentCount << 24) ^ FastHash as group key (wide enough for practical archetype counts).
            GroupByArchetype(values, offsets, createdDestroyed);
        }
        finally
        {
            if (values.Length > 0) ArrayPool<CreatedComponentRef>.Shared.Return(values);
            if (writes.Length > 0) ArrayPool<int>.Shared.Return(writes);
            if (offsets.Length > 0) ArrayPool<int>.Shared.Return(offsets);
        }
    }

    private void GroupByArchetype(
        CreatedComponentRef[] values, int[] offsets, bool[] createdDestroyed)
    {
        // First pass: resolve archetype for each unique component combination.
        // Key format: (componentCount << 24) | (hash & 0xFFFFFF)
        const int maxGroups = 64; // far beyond practical archetype variants per batch
        var groupKeys = ArrayPool<int>.Shared.Rent(maxGroups);
        var groupArchetypes = ArrayPool<Archetype?>.Shared.Rent(maxGroups);
        var groupFirstIdx = ArrayPool<int>.Shared.Rent(maxGroups);
        var entityGroup = ArrayPool<int>.Shared.Rent(_createdCount);
        int groupCount = 0;

        for (var i = 0; i < _createdCount; i++)
        {
            if (createdDestroyed.Length > 0 && createdDestroyed[i])
            {
                _world.ReleaseReservedEntity(_createdEntities[i]);
                entityGroup[i] = -1;
                continue;
            }

            var count = _createdComponentCounts[i];
            if (count == 0)
            {
                _world.MaterializeReservedEntity(_createdEntities[i], Array.Empty<RawComponentValue>(), reservationChecked: true);
                entityGroup[i] = -1;
                continue;
            }

            var span = values.AsSpan(offsets[i], count);
            var key = ComputeGroupKey(span);

            // Find existing group
            int g;
            for (g = 0; g < groupCount; g++)
            {
                if (groupKeys[g] == key && ArchetypeMatchesComponentSpan(groupArchetypes[g]!, span))
                    break;
            }

            if (g == groupCount)
            {
                // New group — resolve archetype once
                var archetype = ResolveArchetypeForSpan(span);
                groupKeys[g] = key;
                groupArchetypes[g] = archetype;
                groupFirstIdx[g] = i;
                groupCount++;
            }

            entityGroup[i] = g;
        }

        // Second pass: materialize entities using their group's archetype.
        for (var i = 0; i < _createdCount; i++)
        {
            var g = entityGroup[i];
            if (g < 0) continue;

            var entity = _createdEntities[i];
            var count = _createdComponentCounts[i];
            var span = values.AsSpan(offsets[i], count);
            _world.MaterializeReservedEntityTyped(entity, groupArchetypes[g]!, span);
        }

        ArrayPool<int>.Shared.Return(groupKeys);
        ArrayPool<Archetype?>.Shared.Return(groupArchetypes);
        ArrayPool<int>.Shared.Return(groupFirstIdx);
        ArrayPool<int>.Shared.Return(entityGroup);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeGroupKey(ReadOnlySpan<CreatedComponentRef> components)
    {
        var hash = components.Length;
        for (var i = 0; i < components.Length; i++)
            hash = hash * 31 + components[i].ComponentType.Value;
        return (components.Length << 24) | (hash & 0xFFFFFF);
    }

    private static bool ArchetypeMatchesComponentSpan(Archetype archetype, ReadOnlySpan<CreatedComponentRef> components)
    {
        var signature = archetype.Signature.AsSpan();
        if (signature.Length != components.Length) return false;
        for (var i = 0; i < components.Length; i++)
            if (signature[i] != components[i].ComponentType) return false;
        return true;
    }

    private Archetype ResolveArchetypeForSpan(ReadOnlySpan<CreatedComponentRef> components)
    {
        // Fast path: last-created archetype hit
        if (_lastCreatedArchetype != null &&
            _lastCreatedComponentCount == components.Length &&
            _lastCreatedFirstComponentType == components[0].ComponentType &&
            SignatureEquals(_lastCreatedArchetype.Signature.AsSpan(), components))
        {
            return _lastCreatedArchetype;
        }

        // Cache lookup
        var hash = ComputeComponentHash(components);
        if (_archetypeCacheGeneration != _world.CreateArchetypeCacheGeneration)
        {
            _archetypeCacheGeneration = _world.CreateArchetypeCacheGeneration;
            _archetypeCacheCount = 0;
        }

        for (var i = 0; i < _archetypeCacheCount; i++)
        {
            var entry = _archetypeCache[i];
            if (entry.Hash == hash && entry.ComponentCount == components.Length &&
                SignatureEquals(entry.Archetype.Signature.AsSpan(), components))
            {
                return entry.Archetype;
            }
        }

        // Create new archetype
        var signatureTypes = new ComponentType[components.Length];
        for (var i = 0; i < components.Length; i++)
            signatureTypes[i] = components[i].ComponentType;
        var archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(signatureTypes));

        // Insert into cache
        if (_archetypeCache.Length == 0)
            _archetypeCache = new ArchetypeCacheEntry[ArchetypeCacheSize];
        var cacheIdx = _archetypeCacheCount < ArchetypeCacheSize
            ? _archetypeCacheCount++
            : hash & (ArchetypeCacheSize - 1);
        _archetypeCache[cacheIdx] = new ArchetypeCacheEntry(hash, components.Length, archetype);

        _lastCreatedArchetype = archetype;
        _lastCreatedComponentCount = components.Length;
        _lastCreatedFirstComponentType = components[0].ComponentType;
        return archetype;
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

    private static int ComputeComponentHash(ReadOnlySpan<CreatedComponentRef> components)
    {
        var hash = components.Length;
        for (var i = 0; i < components.Length; i++)
            hash = hash * 31 + components[i].ComponentType.Value;
        return hash;
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
            if (entry.Kind != StreamOpKind.Remove)
            {
                continue;
            }

            if (IsCreatedEntity(entry.Entity))
            {
                continue;
            }

            _world.RemoveBoxed(entry.Entity, entry.ComponentType);
        }
    }

    private void ApplyHierarchy(bool[] createdDestroyed)
    {
        if (_hierarchyByChild.Count == 0) return;

        foreach (var (child, intent) in _hierarchyByChild)
        {
            // Skip if child is a destroyed created entity
            if (createdDestroyed.Length > 0)
            {
                var childCreatedIdx = GetCreatedIndex(child);
                if (childCreatedIdx >= 0 && createdDestroyed[childCreatedIdx]) continue;
            }

            // Skip if child is a destroyed existing entity (destroyed in this submit)
            if (IsEntityMarkedForDestroy(child)) continue;

            if (intent.IsLinked)
            {
                // Skip if parent is a destroyed created entity
                if (createdDestroyed.Length > 0)
                {
                    var parentCreatedIdx = GetCreatedIndex(intent.Parent);
                    if (parentCreatedIdx >= 0 && createdDestroyed[parentCreatedIdx]) continue;
                }

                _world.Link(intent.Parent, child);
            }
            else
            {
                _world.Unlink(child);
            }
        }
    }

    private bool IsEntityMarkedForDestroy(Entity entity)
    {
        for (var i = 0; i < _entryCount; i++)
        {
            if (_entries[i].Kind == StreamOpKind.Destroy && _entries[i].Entity == entity)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyDestroyEntries()
    {
        for (var i = 0; i < _entryCount; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.Kind != StreamOpKind.Destroy)
            {
                continue;
            }

            if (IsCreatedEntity(entry.Entity))
            {
                continue;
            }

            if (_world.IsAlive(entry.Entity))
            {
                _world.Destroy(entry.Entity);
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

            foreach (var (child, intent) in _hierarchyByChild)
            {
                // Skip if child is a destroyed created entity
                var childCreatedIdx = GetCreatedIndex(child);
                if (childCreatedIdx >= 0 && createdDestroyed.Length > 0 && createdDestroyed[childCreatedIdx]) continue;
                // Skip if child is an existing entity marked for destroy
                if (childCreatedIdx < 0 && IsEntityMarkedForDestroy(child)) continue;

                if (intent.IsLinked)
                {
                    // Skip if parent is a destroyed created entity
                    var parentCreatedIdx = GetCreatedIndex(intent.Parent);
                    if (parentCreatedIdx >= 0 && createdDestroyed.Length > 0 && createdDestroyed[parentCreatedIdx]) continue;

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
        _hierarchyByChild.Clear();
        _lastCreatedArchetype = null;

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
        public abstract int AppendFromArchetype(Archetype archetype, int columnIndex, int rowIndex);
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

        public void AddExisting(StreamOpKind kind, Entity entity, in T component)
        {
            // Grow components array if needed
            if (_count == _components.Length)
            {
                Array.Resize(ref _components, _components.Length == 0 ? 256 : _components.Length * 2);
            }

            var dataIndex = _count;
            _components[dataIndex] = component;
            _count++;

            // Grow existing commands array if needed
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

        public override int AppendFromArchetype(Archetype archetype, int columnIndex, int rowIndex)
        {
            var value = archetype.GetComponentAt<T>(columnIndex, rowIndex);
            return Append(value);
        }

        public override void Clear()
        {
            _count = 0;
            _existingCount = 0;
        }
    }

    private readonly record struct ExistingComponentCommand(StreamOpKind Kind, Entity Entity, int DataIndex);

    internal enum StreamOpKind : byte
    {
        Add,
        Set,
        Remove,
        Destroy,
    }

    private sealed class FrozenStreamState
    {
        public StreamEntry[] Entries = [];
        public int EntryCount;
        public ComponentCommandStore?[] ComponentStores = [];
        public int ComponentCommandCount;
        public Entity[] CreatedEntities = [];
        public int CreatedCount;
        public int[] CreatedComponentCounts = [];
        public CreatedComponentRef[] CreatedComponents = [];
        public int CreatedComponentCount;
        public int[] CreatedLookup = [];
        public int MaxCreatedEntityId;
        public Dictionary<Entity, HierarchyIntent> HierarchyByChild = new();
    }
}
