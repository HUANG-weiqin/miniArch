using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiniArch.Core;

/// <summary>
/// Records deferred world commands.
/// </summary>
public sealed class CommandBuffer
{
    private readonly World _world;
    private readonly CommandBufferEntityAllocator _allocator;
    private readonly ConcurrentDictionary<int, CommandBufferShard> _shards = new();
    private readonly List<CommandBufferShard> _orderedShardsScratch = new(1);
    private readonly Dictionary<Entity, CreatedEntityState> _createdStatesScratch = new(4);
    private readonly Dictionary<EntityComponentKey, RawComponentCommand> _compiledAddsScratch = new(4);
    private readonly Dictionary<EntityComponentKey, RawComponentCommand> _compiledSetsScratch = new(4);
    private readonly Dictionary<EntityComponentKey, RawRemoveCommand> _compiledRemovesScratch = new(4);
    private readonly Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size)> _componentTypeInfoCacheScratch = new(4);
    private readonly HashSet<Entity> _destroyedEntitiesScratch = new(4);
    private readonly Dictionary<Entity, HierarchyIntent> _hierarchyByChildScratch = new(4);
    private readonly FrameDelta _compiledBatch = new();
    private int _nextShardOrder;

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
        GetShard().Creates.Add(entity);
        return entity;
    }

    /// <summary>
    /// Records an add command.
    /// </summary>
    public void Add<T>(Entity entity, T component)
    {
        var componentTypeId = GetComponentTypeId<T>();
        var size = Unsafe.SizeOf<T>();
        var shard = GetShard();
        var offset = shard.AllocateData(size);
        Unsafe.WriteUnaligned(ref shard.Data[offset], component);
        shard.Adds.Add(new RecordedRawCommand(entity, componentTypeId, offset, size));
    }

    /// <summary>
    /// Records a set command.
    /// </summary>
    public void Set<T>(Entity entity, T component)
    {
        var componentTypeId = GetComponentTypeId<T>();
        var size = Unsafe.SizeOf<T>();
        var shard = GetShard();
        var offset = shard.AllocateData(size);
        Unsafe.WriteUnaligned(ref shard.Data[offset], component);
        shard.Sets.Add(new RecordedRawCommand(entity, componentTypeId, offset, size));
    }

    /// <summary>
    /// Records a remove command.
    /// </summary>
    public void Remove<T>(Entity entity)
    {
        var componentTypeId = GetComponentTypeId<T>();
        GetShard().Removes.Add(new RecordedRemoveCommand(entity, componentTypeId));
    }

    /// <summary>
    /// Records a destroy command.
    /// </summary>
    public void Destroy(Entity entity)
    {
        GetShard().Destroys.Add(entity);
    }

    /// <summary>
    /// Records a parent link.
    /// </summary>
    public void Link(Entity parent, Entity child)
    {
        GetShard().HierarchyCommands.Add(new RecordedHierarchyCommand(child, parent, true));
    }

    /// <summary>
    /// Records a parent unlink.
    /// </summary>
    public void Unlink(Entity child)
    {
        GetShard().HierarchyCommands.Add(new RecordedHierarchyCommand(child, default, false));
    }

    /// <summary>
    /// Compiles and replays the buffered commands.
    /// </summary>
    /// <returns><c>true</c> if at least one command was replayed; otherwise, <c>false</c>.</returns>
    public bool Play()
    {
        var compiled = Compile();
        if (compiled.IsEmpty)
        {
            return false;
        }

        _world.Replay(compiled);
        return true;
    }

    /// <summary>
    /// Compiles the buffered commands into a FrameDelta and clears the buffer.
    /// </summary>
    public FrameDelta Compile()
    {
        var shards = GetOrderedShards();
        var counts = CountCommands(CollectionsMarshal.AsSpan(shards));

        var compiledBatch = _compiledBatch;
        compiledBatch.Clear();
        compiledBatch.ReservedEntities.EnsureCapacity(counts.Creates);
        compiledBatch.CreatedEntities.EnsureCapacity(counts.Creates);
        compiledBatch.LinkCommands.EnsureCapacity(counts.HierarchyCommands);
        compiledBatch.UnlinkCommands.EnsureCapacity(counts.HierarchyCommands);
        compiledBatch.AddCommands.EnsureCapacity(counts.Adds);
        compiledBatch.SetCommands.EnsureCapacity(counts.Sets);
        compiledBatch.RemoveCommands.EnsureCapacity(counts.Removes);
        compiledBatch.DestroyedEntities.EnsureCapacity(counts.Destroys);
        compiledBatch.ReleasedEntities.EnsureCapacity(counts.Destroys);

        var reservedEntities = compiledBatch.ReservedEntities;
        var createdEntities = compiledBatch.CreatedEntities;
        var linkCommands = compiledBatch.LinkCommands;
        var unlinkCommands = compiledBatch.UnlinkCommands;
        var addCommands = compiledBatch.AddCommands;
        var setCommands = compiledBatch.SetCommands;
        var removeCommands = compiledBatch.RemoveCommands;
        var destroyedEntityList = compiledBatch.DestroyedEntities;
        var releasedEntities = compiledBatch.ReleasedEntities;

        var createdStates = _createdStatesScratch;
        createdStates.Clear();
        createdStates.EnsureCapacity(counts.Creates);
        var compiledAdds = _compiledAddsScratch;
        compiledAdds.Clear();
        compiledAdds.EnsureCapacity(counts.Adds);
        var compiledSets = _compiledSetsScratch;
        compiledSets.Clear();
        compiledSets.EnsureCapacity(counts.Sets);
        var compiledRemoves = _compiledRemovesScratch;
        compiledRemoves.Clear();
        compiledRemoves.EnsureCapacity(counts.Removes);
        var componentTypeInfoCache = _componentTypeInfoCacheScratch;
        componentTypeInfoCache.Clear();
        componentTypeInfoCache.EnsureCapacity(counts.Adds + counts.Sets + counts.Removes);
        var destroyedEntities = _destroyedEntitiesScratch;
        destroyedEntities.Clear();
        destroyedEntities.EnsureCapacity(counts.Destroys);
        var hierarchyByChild = _hierarchyByChildScratch;
        hierarchyByChild.Clear();
        hierarchyByChild.EnsureCapacity(counts.HierarchyCommands);

        foreach (var shard in shards)
        {
            var creates = shard.Creates;
            for (var index = 0; index < creates.Count; index++)
            {
                var entity = creates[index];
                reservedEntities.Add(entity);
                createdStates.Add(entity, new CreatedEntityState(entity));
            }
        }

        foreach (var shard in shards)
        {
            var adds = shard.Adds;
            var shardData = shard.Data;
            for (var index = 0; index < adds.Count; index++)
            {
                var command = adds[index];
                var (runtimeType, componentType, componentSize) = ResolveComponentTypeInfo(command.ComponentTypeId, componentTypeInfoCache);
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Add(command.ComponentTypeId, runtimeType, componentType, componentSize, shardData, command.DataOffset, command.DataSize);
                    continue;
                }

                compiledAdds[new EntityComponentKey(command.Entity, command.ComponentTypeId)] =
                    new RawComponentCommand(command.Entity, command.ComponentTypeId, runtimeType, componentType, command.DataOffset, command.DataSize, ComponentWriterCache.GetColumnWriter(runtimeType), shardData);
            }
        }

        foreach (var shard in shards)
        {
            var sets = shard.Sets;
            var shardData = shard.Data;
            for (var index = 0; index < sets.Count; index++)
            {
                var command = sets[index];
                var (runtimeType, componentType, componentSize) = ResolveComponentTypeInfo(command.ComponentTypeId, componentTypeInfoCache);
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Set(command.ComponentTypeId, runtimeType, componentType, componentSize, shardData, command.DataOffset, command.DataSize);
                    continue;
                }

                compiledSets[new EntityComponentKey(command.Entity, command.ComponentTypeId)] =
                    new RawComponentCommand(command.Entity, command.ComponentTypeId, runtimeType, componentType, command.DataOffset, command.DataSize, ComponentWriterCache.GetColumnWriter(runtimeType), shardData);
            }
        }

        foreach (var shard in shards)
        {
            var removes = shard.Removes;
            for (var index = 0; index < removes.Count; index++)
            {
                var command = removes[index];
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Remove(command.ComponentTypeId);
                    continue;
                }

                var (runtimeType, componentType, _) = ResolveComponentTypeInfo(command.ComponentTypeId, componentTypeInfoCache);
                compiledRemoves[new EntityComponentKey(command.Entity, command.ComponentTypeId)] =
                    new RawRemoveCommand(command.Entity, command.ComponentTypeId, runtimeType, componentType);
            }
        }

        foreach (var shard in shards)
        {
            var destroys = shard.Destroys;
            for (var index = 0; index < destroys.Count; index++)
            {
                var entity = destroys[index];
                if (createdStates.TryGetValue(entity, out var created))
                {
                    if (!created.Destroyed)
                    {
                        created.Destroyed = true;
                        releasedEntities.Add(entity);
                    }

                    continue;
                }

                destroyedEntities.Add(entity);
            }
        }

        foreach (var shard in shards)
        {
            var hierarchyCommands = shard.HierarchyCommands;
            for (var index = 0; index < hierarchyCommands.Count; index++)
            {
                var command = hierarchyCommands[index];
                if (createdStates.TryGetValue(command.Child, out var childState) && childState.Destroyed)
                {
                    continue;
                }

                if (command.IsLink &&
                    createdStates.TryGetValue(command.Parent, out var parentState) &&
                    parentState.Destroyed)
                {
                    continue;
                }

                hierarchyByChild[command.Child] = command.IsLink
                    ? new HierarchyIntent(true, command.Parent)
                    : new HierarchyIntent(false, default);
            }
        }

        for (var index = 0; index < reservedEntities.Count; index++)
        {
            var entity = reservedEntities[index];
            if (!createdStates.TryGetValue(entity, out var state) || state.Destroyed)
            {
                continue;
            }

            createdEntities.Add(state.ToCompiledEntity());
        }

        foreach (var pair in hierarchyByChild)
        {
            if (pair.Value.IsLinked)
            {
                linkCommands.Add(new FrameLinkCommand(pair.Value.Parent, pair.Key));
                continue;
            }

            unlinkCommands.Add(new FrameUnlinkCommand(pair.Key));
        }

        foreach (var pair in compiledAdds)
        {
            addCommands.Add(pair.Value);
        }

        foreach (var pair in compiledSets)
        {
            setCommands.Add(pair.Value);
        }

        foreach (var pair in compiledRemoves)
        {
            removeCommands.Add(pair.Value);
        }

        foreach (var entity in destroyedEntities)
        {
            destroyedEntityList.Add(entity);
        }

        createdStates.Clear();
        compiledAdds.Clear();
        compiledSets.Clear();
        compiledRemoves.Clear();
        componentTypeInfoCache.Clear();
        destroyedEntities.Clear();
        hierarchyByChild.Clear();
        Clear();
        return compiledBatch;
    }

    private void Clear()
    {
        foreach (var shard in _shards.Values)
        {
            shard.Clear();
        }
    }

    private List<CommandBufferShard> GetOrderedShards()
    {
        var shards = _orderedShardsScratch;
        shards.Clear();

        foreach (var shard in _shards.Values)
        {
            shards.Add(shard);
        }

        shards.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        return shards;
    }

    private static CommandCounts CountCommands(ReadOnlySpan<CommandBufferShard> shards)
    {
        var counts = new CommandCounts();
        for (var index = 0; index < shards.Length; index++)
        {
            var shard = shards[index];
            counts.Creates += shard.Creates.Count;
            counts.HierarchyCommands += shard.HierarchyCommands.Count;
            counts.Adds += shard.Adds.Count;
            counts.Sets += shard.Sets.Count;
            counts.Removes += shard.Removes.Count;
            counts.Destroys += shard.Destroys.Count;
        }

        return counts;
    }

    private (Type RuntimeType, ComponentType ComponentType, int Size) ResolveComponentTypeInfo(
        int componentTypeId, Dictionary<int, (Type RuntimeType, ComponentType ComponentType, int Size)> cache)
    {
        if (cache.TryGetValue(componentTypeId, out var info))
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
        info = (runtimeType, componentType, size);
        cache.Add(componentTypeId, info);
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

    private CommandBufferShard GetShard()
    {
        var threadId = Environment.CurrentManagedThreadId;
        return _shards.GetOrAdd(
            threadId,
            _ => new CommandBufferShard(Interlocked.Increment(ref _nextShardOrder)));
    }

    private sealed class CreatedEntityState
    {
        private Dictionary<int, RawComponentValue>? _components;

        public CreatedEntityState(Entity entity)
        {
            Entity = entity;
        }

        public Entity Entity { get; }

        public bool Destroyed { get; set; }

        public void Add(int componentTypeId, Type runtimeType, ComponentType componentType, int size, byte[] sourceData, int sourceOffset, int sourceSize)
        {
            (_components ??= [])[componentTypeId] = new RawComponentValue(componentTypeId, runtimeType, componentType, size, sourceData, sourceOffset, sourceSize);
        }

        public void Set(int componentTypeId, Type runtimeType, ComponentType componentType, int size, byte[] sourceData, int sourceOffset, int sourceSize)
        {
            (_components ??= [])[componentTypeId] = new RawComponentValue(componentTypeId, runtimeType, componentType, size, sourceData, sourceOffset, sourceSize);
        }

        public void Remove(int componentTypeId)
        {
            _components?.Remove(componentTypeId);
        }

        public RawCreatedEntity ToCompiledEntity()
        {
            if (_components is null || _components.Count == 0)
            {
                return new RawCreatedEntity(Entity, Signature.Empty, Array.Empty<RawComponentValue>());
            }

            var count = _components.Count;
            var components = ArrayPool<RawComponentValue>.Shared.Rent(count);
            var componentTypes = ArrayPool<ComponentType>.Shared.Rent(count);
            try
            {
                var index = 0;
                var allComponentTypesResolved = true;
                foreach (var pair in _components)
                {
                    var value = pair.Value;
                    components[index] = value;
                    componentTypes[index] = value.ComponentType;
                    allComponentTypesResolved &= value.ComponentType.IsValid;
                    index++;
                }

                Signature? signature = null;
                if (allComponentTypesResolved)
                {
                    Array.Sort(componentTypes, components, 0, count);

                    var signatureTypes = new ComponentType[count];
                    Array.Copy(componentTypes, signatureTypes, count);
                    signature = Signature.CreateNormalized(signatureTypes);
                }

                var entityComponents = new RawComponentValue[count];
                Array.Copy(components, entityComponents, count);

                return new RawCreatedEntity(Entity, signature, entityComponents);
            }
            finally
            {
                ArrayPool<RawComponentValue>.Shared.Return(components);
                ArrayPool<ComponentType>.Shared.Return(componentTypes);
            }
        }
    }

    private readonly record struct EntityComponentKey(Entity Entity, int ComponentTypeId);

    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);

    private struct CommandCounts
    {
        public int Creates;
        public int HierarchyCommands;
        public int Adds;
        public int Sets;
        public int Removes;
        public int Destroys;
    }

    private static class ComponentTypeCache<T>
    {
        public static ComponentTypeIdCacheEntry? Entry;
    }

    private sealed record ComponentTypeIdCacheEntry(ComponentRegistry Registry, int ComponentTypeId);
}
