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
    private readonly Dictionary<EntityComponentKey, CompiledComponentCommand> _compiledAddsScratch = new(4);
    private readonly Dictionary<EntityComponentKey, CompiledComponentCommand> _compiledSetsScratch = new(4);
    private readonly Dictionary<EntityComponentKey, CompiledRemoveCommand> _compiledRemovesScratch = new(4);
    private readonly Dictionary<int, (Type RuntimeType, ComponentType ComponentType)> _componentTypeInfoCacheScratch = new(4);
    private readonly HashSet<Entity> _destroyedEntitiesScratch = new(4);
    private readonly Dictionary<Entity, HierarchyIntent> _hierarchyByChildScratch = new(4);
    private readonly CompiledCommandBatch _compiledBatch = new();
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
        GetShard().Adds.Add(new RecordedComponentCommand(entity, componentTypeId, component));
    }

    /// <summary>
    /// Records a set command.
    /// </summary>
    public void Set<T>(Entity entity, T component)
    {
        var componentTypeId = GetComponentTypeId<T>();
        GetShard().Sets.Add(new RecordedComponentCommand(entity, componentTypeId, component));
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
    /// Compiles the buffered commands.
    /// </summary>
    public FrameCommands Playback()
    {
        var compiled = Compile();
        var frame = compiled.ToFrameCommands();
        Clear();
        return frame;
    }

    /// <summary>
    /// Compiles the buffered commands into a bidirectional world delta.
    /// </summary>
    public WorldDelta PlaybackDelta()
    {
        var compiled = Compile();
        var frame = compiled.ToFrameCommands();
        var delta = _world.CaptureDelta(in frame);
        Clear();
        return delta;
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

        try
        {
            _world.Replay(compiled);
            return true;
        }
        finally
        {
            Clear();
        }
    }

    /// <summary>
    /// Replays the buffer and captures reverse commands.
    /// </summary>
    public ReverseFrameCommands PlayWithReverse()
    {
        var compiled = Compile();
        var frame = compiled.ToFrameCommands();

        try
        {
            return _world.ReplayWithReverse(in frame);
        }
        finally
        {
            Clear();
        }
    }

    private CompiledCommandBatch Compile()
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
            for (var index = 0; index < adds.Count; index++)
            {
                var command = adds[index];
                var (runtimeType, componentType) = ResolveComponentTypeInfo(command.ComponentTypeId, componentTypeInfoCache);
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Add(command.ComponentTypeId, runtimeType, componentType, command.Value);
                    continue;
                }

                compiledAdds[new EntityComponentKey(command.Entity, command.ComponentTypeId)] =
                    new CompiledComponentCommand(command.Entity, command.ComponentTypeId, runtimeType, componentType, command.Value);
            }
        }

        foreach (var shard in shards)
        {
            var sets = shard.Sets;
            for (var index = 0; index < sets.Count; index++)
            {
                var command = sets[index];
                var (runtimeType, componentType) = ResolveComponentTypeInfo(command.ComponentTypeId, componentTypeInfoCache);
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Set(command.ComponentTypeId, runtimeType, componentType, command.Value);
                    continue;
                }

                compiledSets[new EntityComponentKey(command.Entity, command.ComponentTypeId)] =
                    new CompiledComponentCommand(command.Entity, command.ComponentTypeId, runtimeType, componentType, command.Value);
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

                var (runtimeType, componentType) = ResolveComponentTypeInfo(command.ComponentTypeId, componentTypeInfoCache);
                compiledRemoves[new EntityComponentKey(command.Entity, command.ComponentTypeId)] =
                    new CompiledRemoveCommand(command.Entity, command.ComponentTypeId, runtimeType, componentType);
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
        return compiledBatch;
    }

    private void Clear()
    {
        foreach (var shard in _shards.Values)
        {
            ClearShard(shard);
        }
    }

    private static void ClearShard(CommandBufferShard shard)
    {
        shard.Creates.Clear();
        shard.HierarchyCommands.Clear();
        shard.Adds.Clear();
        shard.Sets.Clear();
        shard.Removes.Clear();
        shard.Destroys.Clear();
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

    private (Type RuntimeType, ComponentType ComponentType) ResolveComponentTypeInfo(int componentTypeId, Dictionary<int, (Type RuntimeType, ComponentType ComponentType)> cache)
    {
        if (cache.TryGetValue(componentTypeId, out var info))
        {
            return info;
        }

        // componentTypeId comes from GetOrCreate<T>().Value which equals ComponentType.Value,
        // so we can reconstruct ComponentType directly and only look up the runtime Type.
        var componentType = (ComponentType)componentTypeId;
        if (!_world.Components.TryGetType(componentType, out var runtimeType))
        {
            runtimeType = typeof(object);
            componentType = default;
        }

        info = (runtimeType, componentType);
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
        private Dictionary<int, CompiledComponentValue>? _components;

        public CreatedEntityState(Entity entity)
        {
            Entity = entity;
        }

        public Entity Entity { get; }

        public bool Destroyed { get; set; }

        public void Add(int componentTypeId, Type runtimeType, ComponentType componentType, object? value)
        {
            (_components ??= [])[componentTypeId] = new CompiledComponentValue(componentTypeId, runtimeType, componentType, value);
        }

        public void Set(int componentTypeId, Type runtimeType, ComponentType componentType, object? value)
        {
            (_components ??= [])[componentTypeId] = new CompiledComponentValue(componentTypeId, runtimeType, componentType, value);
        }

        public void Remove(int componentTypeId)
        {
            _components?.Remove(componentTypeId);
        }

        public CompiledCreatedEntity ToCompiledEntity()
        {
            if (_components is null || _components.Count == 0)
            {
                return new CompiledCreatedEntity(Entity, Signature.Empty, Array.Empty<CompiledComponentValue>());
            }

            var count = _components.Count;
            var components = ArrayPool<CompiledComponentValue>.Shared.Rent(count);
            var componentTypes = ArrayPool<ComponentType>.Shared.Rent(count);
            try
            {
                var index = 0;
                var allComponentTypesResolved = true;
                foreach (var pair in _components)
                {
                    componentTypes[index] = pair.Value.ComponentType;
                    components[index] = pair.Value;
                    allComponentTypesResolved &= pair.Value.ComponentType.IsValid;
                    index++;
                }

                Signature? signature = null;
                if (allComponentTypesResolved)
                {
                    // Sort the rented arrays in-place (only first `count` elements matter).
                    Array.Sort(componentTypes, components, 0, count);

                    // Signature.CreateNormalized takes ownership of the array,
                    // so we must provide a correctly-sized one (not a pooled oversized one).
                    var signatureTypes = new ComponentType[count];
                    Array.Copy(componentTypes, signatureTypes, count);
                    signature = Signature.CreateNormalized(signatureTypes);
                }

                // Copy pooled data into an exactly-sized array for the entity.
                var entityComponents = new CompiledComponentValue[count];
                Array.Copy(components, entityComponents, count);

                return new CompiledCreatedEntity(Entity, signature, entityComponents);
            }
            finally
            {
                ArrayPool<CompiledComponentValue>.Shared.Return(components);
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

    internal readonly record struct CompiledComponentValue(int ComponentTypeId, Type RuntimeType, ComponentType ComponentType, object? Value);

    internal readonly record struct CompiledCreatedEntity(Entity Entity, Signature? Signature, CompiledComponentValue[] Components)
    {
        public FrameCreatedEntity ToFrame()
        {
            if (Components.Length == 0)
            {
                return new FrameCreatedEntity(Entity, Array.Empty<FrameComponentValue>());
            }

            var components = new FrameComponentValue[Components.Length];
            for (var index = 0; index < Components.Length; index++)
            {
                var component = Components[index];
                components[index] = new FrameComponentValue(component.RuntimeType, component.Value);
            }

            Array.Sort(components, static (left, right) =>
                StringComparer.Ordinal.Compare(left.ComponentType.FullName, right.ComponentType.FullName));

            return new FrameCreatedEntity(Entity, components);
        }
    }

    internal readonly record struct CompiledComponentCommand(Entity Entity, int ComponentTypeId, Type RuntimeType, ComponentType ComponentType, object? Value)
    {
        public FrameEntityComponentCommand ToFrame()
        {
            return new FrameEntityComponentCommand(Entity, RuntimeType, Value);
        }
    }

    internal readonly record struct CompiledRemoveCommand(Entity Entity, int ComponentTypeId, Type RuntimeType, ComponentType ComponentType)
    {
        public FrameEntityRemoveCommand ToFrame()
        {
            return new FrameEntityRemoveCommand(Entity, RuntimeType);
        }
    }

    internal sealed class CompiledCommandBatch
    {
        public List<Entity> ReservedEntities { get; } = new(4);

        public List<CompiledCreatedEntity> CreatedEntities { get; } = new(4);

        public List<FrameLinkCommand> LinkCommands { get; } = new(4);

        public List<FrameUnlinkCommand> UnlinkCommands { get; } = new(4);

        public List<CompiledComponentCommand> AddCommands { get; } = new(4);

        public List<CompiledComponentCommand> SetCommands { get; } = new(4);

        public List<CompiledRemoveCommand> RemoveCommands { get; } = new(4);

        public List<Entity> DestroyedEntities { get; } = new(4);

        public List<Entity> ReleasedEntities { get; } = new(4);

        public void Clear()
        {
            ReservedEntities.Clear();
            CreatedEntities.Clear();
            LinkCommands.Clear();
            UnlinkCommands.Clear();
            AddCommands.Clear();
            SetCommands.Clear();
            RemoveCommands.Clear();
            DestroyedEntities.Clear();
            ReleasedEntities.Clear();
        }

        public bool IsEmpty =>
            ReservedEntities.Count == 0 &&
            CreatedEntities.Count == 0 &&
            LinkCommands.Count == 0 &&
            UnlinkCommands.Count == 0 &&
            AddCommands.Count == 0 &&
            SetCommands.Count == 0 &&
            RemoveCommands.Count == 0 &&
            DestroyedEntities.Count == 0 &&
            ReleasedEntities.Count == 0;

        public FrameCommands ToFrameCommands()
        {
            var createdEntities = new FrameCreatedEntity[CreatedEntities.Count];
            for (var index = 0; index < CreatedEntities.Count; index++)
            {
                createdEntities[index] = CreatedEntities[index].ToFrame();
            }

            var addCommands = new FrameEntityComponentCommand[AddCommands.Count];
            for (var index = 0; index < AddCommands.Count; index++)
            {
                addCommands[index] = AddCommands[index].ToFrame();
            }

            var setCommands = new FrameEntityComponentCommand[SetCommands.Count];
            for (var index = 0; index < SetCommands.Count; index++)
            {
                setCommands[index] = SetCommands[index].ToFrame();
            }

            var removeCommands = new FrameEntityRemoveCommand[RemoveCommands.Count];
            for (var index = 0; index < RemoveCommands.Count; index++)
            {
                removeCommands[index] = RemoveCommands[index].ToFrame();
            }

            var state = new FrameCommandsState(
                ReservedEntities.ToArray(),
                createdEntities,
                LinkCommands.ToArray(),
                UnlinkCommands.ToArray(),
                addCommands,
                setCommands,
                removeCommands,
                DestroyedEntities.ToArray(),
                ReleasedEntities.ToArray());

            return new FrameCommands(state);
        }
    }

    /// <summary>
    /// Per-type cache for component type id resolution, avoiding concurrent dictionary lookups after warmup.
    /// </summary>
    private static class ComponentTypeCache<T>
    {
        public static ComponentTypeIdCacheEntry? Entry;
    }

    private sealed record ComponentTypeIdCacheEntry(ComponentRegistry Registry, int ComponentTypeId);
}
