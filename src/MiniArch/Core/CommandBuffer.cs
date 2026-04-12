using System.Collections.Concurrent;
using System.Threading;

namespace MiniArch.Core;

public sealed class CommandBuffer
{
    private readonly World _world;
    private readonly CommandBufferEntityAllocator _allocator;
    private readonly ConcurrentDictionary<int, CommandBufferShard> _shards = new();
    private int _nextShardOrder;
    private int _playedBack;

    public CommandBuffer(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _allocator = new CommandBufferEntityAllocator(world);
    }

    public Entity Create()
    {
        var entity = _allocator.ReserveEntity();
        GetShard().Creates.Add(entity);
        return entity;
    }

    public void Add<T>(Entity entity, T component)
    {
        GetShard().Adds.Add(new RecordedComponentCommand(entity, typeof(T), component));
    }

    public void Set<T>(Entity entity, T component)
    {
        GetShard().Sets.Add(new RecordedComponentCommand(entity, typeof(T), component));
    }

    public void Remove<T>(Entity entity)
    {
        GetShard().Removes.Add(new RecordedRemoveCommand(entity, typeof(T)));
    }

    public void Destroy(Entity entity)
    {
        GetShard().Destroys.Add(entity);
    }

    public void Link(Entity parent, Entity child)
    {
        GetShard().HierarchyCommands.Add(new RecordedHierarchyCommand(child, parent, true));
    }

    public void Unlink(Entity child)
    {
        GetShard().HierarchyCommands.Add(new RecordedHierarchyCommand(child, default, false));
    }

    public FrameCommands Playback()
    {
        var compiled = Compile();
        return compiled.ToFrameCommands();
    }

    public void Play()
    {
        var compiled = Compile();
        _world.Replay(compiled);
    }

    private CompiledCommandBatch Compile()
    {
        if (Interlocked.Exchange(ref _playedBack, 1) != 0)
        {
            throw new InvalidOperationException("CommandBuffer can only be consumed once.");
        }

        var shards = _shards.Values.OrderBy(static shard => shard.Order).ToArray();

        var createdEntities = new List<Entity>();
        var hierarchyCommands = new List<RecordedHierarchyCommand>();
        var addCommands = new List<RecordedComponentCommand>();
        var setCommands = new List<RecordedComponentCommand>();
        var removeCommands = new List<RecordedRemoveCommand>();
        var destroyCommands = new List<Entity>();

        foreach (var shard in shards)
        {
            createdEntities.AddRange(shard.Creates);
            hierarchyCommands.AddRange(shard.HierarchyCommands);
            addCommands.AddRange(shard.Adds);
            setCommands.AddRange(shard.Sets);
            removeCommands.AddRange(shard.Removes);
            destroyCommands.AddRange(shard.Destroys);
        }

        var createdStates = createdEntities.ToDictionary(
            static entity => entity,
            static entity => new CreatedEntityState(entity));

        var compiledAdds = new Dictionary<EntityComponentKey, object?>();
        var compiledSets = new Dictionary<EntityComponentKey, object?>();
        var compiledRemoves = new HashSet<EntityComponentKey>();

        foreach (var command in addCommands)
        {
            if (createdStates.TryGetValue(command.Entity, out var created))
            {
                created.Add(command.ComponentType, command.Value);
                continue;
            }

            compiledAdds[new EntityComponentKey(command.Entity, command.ComponentType)] = command.Value;
        }

        foreach (var command in setCommands)
        {
            if (createdStates.TryGetValue(command.Entity, out var created))
            {
                created.Set(command.ComponentType, command.Value);
                continue;
            }

            compiledSets[new EntityComponentKey(command.Entity, command.ComponentType)] = command.Value;
        }

        foreach (var command in removeCommands)
        {
            if (createdStates.TryGetValue(command.Entity, out var created))
            {
                created.Remove(command.ComponentType);
                continue;
            }

            compiledRemoves.Add(new EntityComponentKey(command.Entity, command.ComponentType));
        }

        var releasedEntities = new List<Entity>();
        var destroyedEntities = new HashSet<Entity>();
        foreach (var entity in destroyCommands)
        {
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

        var hierarchyByChild = new Dictionary<Entity, HierarchyIntent>();
        foreach (var command in hierarchyCommands)
        {
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

        return new CompiledCommandBatch(
            createdEntities,
            createdStates.Values.Where(static state => !state.Destroyed).Select(static state => state.ToFrame()).ToList(),
            hierarchyByChild
                .Where(static pair => pair.Value.IsLinked)
                .Select(static pair => new FrameLinkCommand(pair.Value.Parent, pair.Key))
                .ToList(),
            hierarchyByChild
                .Where(static pair => !pair.Value.IsLinked)
                .Select(static pair => new FrameUnlinkCommand(pair.Key))
                .ToList(),
            compiledAdds
                .Select(static pair => new FrameEntityComponentCommand(pair.Key.Entity, pair.Key.ComponentType, pair.Value))
                .ToList(),
            compiledSets
                .Select(static pair => new FrameEntityComponentCommand(pair.Key.Entity, pair.Key.ComponentType, pair.Value))
                .ToList(),
            compiledRemoves
                .Select(static key => new FrameEntityRemoveCommand(key.Entity, key.ComponentType))
                .ToList(),
            destroyedEntities.ToList(),
            releasedEntities);
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
        private readonly Dictionary<Type, object?> _components = [];

        public CreatedEntityState(Entity entity)
        {
            Entity = entity;
        }

        public Entity Entity { get; }

        public bool Destroyed { get; set; }

        public void Add(Type componentType, object? value)
        {
            _components[componentType] = value;
        }

        public void Set(Type componentType, object? value)
        {
            _components[componentType] = value;
        }

        public void Remove(Type componentType)
        {
            _components.Remove(componentType);
        }

        public FrameCreatedEntity ToFrame()
        {
            var components = _components
                .OrderBy(static pair => pair.Key.FullName, StringComparer.Ordinal)
                .Select(static pair => new FrameComponentValue(pair.Key, pair.Value))
                .ToArray();

            return new FrameCreatedEntity(Entity, components);
        }
    }

    private readonly record struct EntityComponentKey(Entity Entity, Type ComponentType);

    private readonly record struct HierarchyIntent(bool IsLinked, Entity Parent);

    internal sealed class CompiledCommandBatch
    {
        public CompiledCommandBatch(
            List<Entity> reservedEntities,
            List<FrameCreatedEntity> createdEntities,
            List<FrameLinkCommand> linkCommands,
            List<FrameUnlinkCommand> unlinkCommands,
            List<FrameEntityComponentCommand> addCommands,
            List<FrameEntityComponentCommand> setCommands,
            List<FrameEntityRemoveCommand> removeCommands,
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

        public List<FrameCreatedEntity> CreatedEntities { get; }

        public List<FrameLinkCommand> LinkCommands { get; }

        public List<FrameUnlinkCommand> UnlinkCommands { get; }

        public List<FrameEntityComponentCommand> AddCommands { get; }

        public List<FrameEntityComponentCommand> SetCommands { get; }

        public List<FrameEntityRemoveCommand> RemoveCommands { get; }

        public List<Entity> DestroyedEntities { get; }

        public List<Entity> ReleasedEntities { get; }

        public FrameCommands ToFrameCommands()
        {
            var state = new FrameCommandsState(
                ReservedEntities.ToArray(),
                CreatedEntities.ToArray(),
                LinkCommands.ToArray(),
                UnlinkCommands.ToArray(),
                AddCommands.ToArray(),
                SetCommands.ToArray(),
                RemoveCommands.ToArray(),
                DestroyedEntities.ToArray(),
                ReleasedEntities.ToArray());

            return new FrameCommands(state);
        }
    }
}
