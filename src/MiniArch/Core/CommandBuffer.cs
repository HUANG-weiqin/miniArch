using System.Collections.Concurrent;
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
    private int _nextShardOrder;
    private int _playedBack;

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
        GetShard().Adds.Add(new RecordedComponentCommand(entity, typeof(T), component));
    }

    /// <summary>
    /// Records a set command.
    /// </summary>
    public void Set<T>(Entity entity, T component)
    {
        GetShard().Sets.Add(new RecordedComponentCommand(entity, typeof(T), component));
    }

    /// <summary>
    /// Records a remove command.
    /// </summary>
    public void Remove<T>(Entity entity)
    {
        GetShard().Removes.Add(new RecordedRemoveCommand(entity, typeof(T)));
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
        return compiled.ToFrameCommands();
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
    /// Replays the buffer and captures reverse commands.
    /// </summary>
    public ReverseFrameCommands PlayWithReverse()
    {
        var frame = Playback();
        return _world.ReplayWithReverse(in frame);
    }

    private CompiledCommandBatch Compile()
    {
        if (Interlocked.Exchange(ref _playedBack, 1) != 0)
        {
            throw new InvalidOperationException("CommandBuffer can only be consumed once.");
        }

        var shards = GetOrderedShards();
        var counts = CountCommands(shards);

        var reservedEntities = new List<Entity>(counts.Creates);
        var createdStates = new Dictionary<Entity, CreatedEntityState>(counts.Creates);

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

        var compiledAdds = new Dictionary<EntityComponentKey, CompiledComponentCommand>(counts.Adds);
        var compiledSets = new Dictionary<EntityComponentKey, CompiledComponentCommand>(counts.Sets);
        var compiledRemoves = new HashSet<EntityComponentKey>(counts.Removes);
        var componentTypeCache = new Dictionary<Type, ComponentType>();

        foreach (var shard in shards)
        {
            var adds = shard.Adds;
            for (var index = 0; index < adds.Count; index++)
            {
                var command = adds[index];
                var componentType = ResolveComponentType(command.ComponentType, componentTypeCache);
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Add(command.ComponentType, componentType, command.Value);
                    continue;
                }

                compiledAdds[new EntityComponentKey(command.Entity, command.ComponentType)] =
                    new CompiledComponentCommand(command.Entity, command.ComponentType, componentType, command.Value);
            }
        }

        foreach (var shard in shards)
        {
            var sets = shard.Sets;
            for (var index = 0; index < sets.Count; index++)
            {
                var command = sets[index];
                var componentType = ResolveComponentType(command.ComponentType, componentTypeCache);
                if (createdStates.TryGetValue(command.Entity, out var created))
                {
                    created.Set(command.ComponentType, componentType, command.Value);
                    continue;
                }

                compiledSets[new EntityComponentKey(command.Entity, command.ComponentType)] =
                    new CompiledComponentCommand(command.Entity, command.ComponentType, componentType, command.Value);
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
                    created.Remove(command.ComponentType);
                    continue;
                }

                compiledRemoves.Add(new EntityComponentKey(command.Entity, command.ComponentType));
            }
        }

        var releasedEntities = new List<Entity>(counts.Destroys);
        var destroyedEntities = new HashSet<Entity>();
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

        var hierarchyByChild = new Dictionary<Entity, HierarchyIntent>(counts.HierarchyCommands);
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

        var createdEntities = new List<CompiledCreatedEntity>(reservedEntities.Count - releasedEntities.Count);
        for (var index = 0; index < reservedEntities.Count; index++)
        {
            var entity = reservedEntities[index];
            if (!createdStates.TryGetValue(entity, out var state) || state.Destroyed)
            {
                continue;
            }

            createdEntities.Add(state.ToCompiledEntity());
        }

        var linkCommands = new List<FrameLinkCommand>(hierarchyByChild.Count);
        var unlinkCommands = new List<FrameUnlinkCommand>(hierarchyByChild.Count);
        foreach (var pair in hierarchyByChild)
        {
            if (pair.Value.IsLinked)
            {
                linkCommands.Add(new FrameLinkCommand(pair.Value.Parent, pair.Key));
                continue;
            }

            unlinkCommands.Add(new FrameUnlinkCommand(pair.Key));
        }

        var addCommands = new List<CompiledComponentCommand>(compiledAdds.Count);
        foreach (var command in compiledAdds.Values)
        {
            addCommands.Add(command);
        }

        var setCommands = new List<CompiledComponentCommand>(compiledSets.Count);
        foreach (var command in compiledSets.Values)
        {
            setCommands.Add(command);
        }

        var removeCommands = new List<CompiledRemoveCommand>(compiledRemoves.Count);
        foreach (var key in compiledRemoves)
        {
            removeCommands.Add(new CompiledRemoveCommand(key.Entity, key.ComponentType, ResolveComponentType(key.ComponentType, componentTypeCache)));
        }

        var destroyedEntityList = new List<Entity>(destroyedEntities.Count);
        foreach (var entity in destroyedEntities)
        {
            destroyedEntityList.Add(entity);
        }

        return new CompiledCommandBatch(
            reservedEntities,
            createdEntities,
            linkCommands,
            unlinkCommands,
            addCommands,
            setCommands,
            removeCommands,
            destroyedEntityList,
            releasedEntities);
    }

    private CommandBufferShard[] GetOrderedShards()
    {
        var shards = _shards.Values.ToArray();
        Array.Sort(shards, static (left, right) => left.Order.CompareTo(right.Order));
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

    private ComponentType ResolveComponentType(Type runtimeType, Dictionary<Type, ComponentType> cache)
    {
        if (cache.TryGetValue(runtimeType, out var componentType))
        {
            return componentType;
        }

        componentType = _world.Components.TryGetId(runtimeType, out var resolved)
            ? resolved
            : (ComponentType)(-1);
        cache.Add(runtimeType, componentType);
        return componentType;
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
        private Dictionary<Type, CompiledComponentValue>? _components;

        public CreatedEntityState(Entity entity)
        {
            Entity = entity;
        }

        public Entity Entity { get; }

        public bool Destroyed { get; set; }

        public void Add(Type runtimeType, ComponentType componentType, object? value)
        {
            (_components ??= [])[runtimeType] = new CompiledComponentValue(runtimeType, componentType, value);
        }

        public void Set(Type runtimeType, ComponentType componentType, object? value)
        {
            (_components ??= [])[runtimeType] = new CompiledComponentValue(runtimeType, componentType, value);
        }

        public void Remove(Type runtimeType)
        {
            _components?.Remove(runtimeType);
        }

        public CompiledCreatedEntity ToCompiledEntity()
        {
            if (_components is null || _components.Count == 0)
            {
                return new CompiledCreatedEntity(Entity, Signature.Empty, Array.Empty<CompiledComponentValue>());
            }

            var components = new CompiledComponentValue[_components.Count];
            var componentTypes = new ComponentType[_components.Count];
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
                Array.Sort(componentTypes, components);
                signature = Signature.CreateNormalized(componentTypes);
            }

            return new CompiledCreatedEntity(Entity, signature, components);
        }
    }

    private readonly record struct EntityComponentKey(Entity Entity, Type ComponentType);

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

    internal readonly record struct CompiledComponentValue(Type RuntimeType, ComponentType ComponentType, object? Value);

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

    internal readonly record struct CompiledComponentCommand(Entity Entity, Type RuntimeType, ComponentType ComponentType, object? Value)
    {
        public FrameEntityComponentCommand ToFrame()
        {
            return new FrameEntityComponentCommand(Entity, RuntimeType, Value);
        }
    }

    internal readonly record struct CompiledRemoveCommand(Entity Entity, Type RuntimeType, ComponentType ComponentType)
    {
        public FrameEntityRemoveCommand ToFrame()
        {
            return new FrameEntityRemoveCommand(Entity, RuntimeType);
        }
    }

    internal sealed class CompiledCommandBatch
    {
        public CompiledCommandBatch(
            List<Entity> reservedEntities,
            List<CompiledCreatedEntity> createdEntities,
            List<FrameLinkCommand> linkCommands,
            List<FrameUnlinkCommand> unlinkCommands,
            List<CompiledComponentCommand> addCommands,
            List<CompiledComponentCommand> setCommands,
            List<CompiledRemoveCommand> removeCommands,
            List<Entity> destroyedEntities,
            List<Entity> releasedEntities)
        {
            ReservedEntities = reservedEntities;
            CreatedEntities = createdEntities;
            LinkCommands = linkCommands;
            UnlinkCommands = unlinkCommands;
            AddCommands = addCommands;
            SetCommands = setCommands;
            RemoveCommands = removeCommands;
            DestroyedEntities = destroyedEntities;
            ReleasedEntities = releasedEntities;
        }

        public List<Entity> ReservedEntities { get; }

        public List<CompiledCreatedEntity> CreatedEntities { get; }

        public List<FrameLinkCommand> LinkCommands { get; }

        public List<FrameUnlinkCommand> UnlinkCommands { get; }

        public List<CompiledComponentCommand> AddCommands { get; }

        public List<CompiledComponentCommand> SetCommands { get; }

        public List<CompiledRemoveCommand> RemoveCommands { get; }

        public List<Entity> DestroyedEntities { get; }

        public List<Entity> ReleasedEntities { get; }

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
}
