using System.Linq;

namespace MiniArch.Ecs;

/// <summary>
/// User-facing ECS world facade for gameplay code.
/// </summary>
public sealed class World
{
    private readonly MiniArch.Core.World _world;

    /// <summary>
    /// Creates a world with the default chunk and entity capacities.
    /// </summary>
    public World(int chunkCapacity = 128, int entityCapacity = 64)
    {
        _world = new MiniArch.Core.World(chunkCapacity, entityCapacity);
    }

    internal World(MiniArch.Core.World world)
    {
        _world = world;
    }

    /// <summary>
    /// Gets the underlying advanced world.
    /// </summary>
    public MiniArch.Core.World Advanced => _world;

    /// <summary>
    /// Creates an entity with no components.
    /// </summary>
    public Entity Create() => Entity.FromCore(_world.Create());

    /// <summary>
    /// Creates an entity with one component.
    /// </summary>
    public Entity Create<T1>(T1 component1) => Entity.FromCore(_world.Create(component1));

    /// <summary>
    /// Creates an entity with two components.
    /// </summary>
    public Entity Create<T1, T2>(T1 component1, T2 component2) => Entity.FromCore(_world.Create(component1, component2));

    /// <summary>
    /// Creates an entity with three components.
    /// </summary>
    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3) => Entity.FromCore(_world.Create(component1, component2, component3));

    /// <summary>
    /// Adds a component to an entity.
    /// </summary>
    public void Add<T>(Entity entity, T component) => _world.Add(entity.AsCore(), component);

    /// <summary>
    /// Sets a component value on an entity.
    /// </summary>
    public void Set<T>(Entity entity, T component) => _world.Set(entity.AsCore(), component);

    /// <summary>
    /// Removes a component from an entity.
    /// </summary>
    public void Remove<T>(Entity entity) => _world.Remove<T>(entity.AsCore());

    /// <summary>
    /// Destroys an entity.
    /// </summary>
    public void Destroy(Entity entity) => _world.Destroy(entity.AsCore());

    /// <summary>
    /// Links a child entity to a parent entity.
    /// </summary>
    public void Link(Entity parent, Entity child) => _world.Link(parent.AsCore(), child.AsCore());

    /// <summary>
    /// Removes the parent link from an entity.
    /// </summary>
    public void Unlink(Entity child) => _world.Unlink(child.AsCore());

    /// <summary>
    /// Tries to resolve the parent of a child entity.
    /// </summary>
    public bool TryGetParent(Entity child, out Entity parent)
    {
        var result = _world.TryGetParent(child.AsCore(), out var resolved);
        parent = Entity.FromCore(resolved);
        return result;
    }

    /// <summary>
    /// Gets the current children of a parent entity.
    /// </summary>
    public List<Entity> GetChildren(Entity parent)
    {
        return _world.GetChildren(parent.AsCore()).Select(Entity.FromCore).ToList();
    }

    /// <summary>
    /// Returns whether the entity is still alive in this world.
    /// </summary>
    public bool IsAlive(Entity entity) => _world.IsAlive(entity.AsCore());

    /// <summary>
    /// Tries to read a component directly from an entity.
    /// </summary>
    public bool TryGet<T>(Entity entity, out T component)
    {
        if (!_world.TryGetLocation(entity.AsCore(), out var info))
        {
            component = default!;
            return false;
        }

        var componentType = _world.Components.GetOrCreate<T>();
        if (!info.Archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            component = default!;
            return false;
        }

        component = info.Archetype
            .GetChunk(info.ChunkIndex)
            .GetComponentAt<T>(componentIndex, info.RowIndex);
        return true;
    }

    /// <summary>
    /// Creates a single-component query.
    /// </summary>
    public Query<T> Query<T>()
    {
        return new Query<T>(_world.Query<T>(), _world.Components.GetOrCreate<T>());
    }

    /// <summary>
    /// Creates a two-component query.
    /// </summary>
    public Query<T1, T2> Query<T1, T2>()
    {
        return new Query<T1, T2>(
            _world.Query<T1, T2>(),
            _world.Components.GetOrCreate<T1>(),
            _world.Components.GetOrCreate<T2>());
    }

    /// <summary>
    /// Creates an entity query from a reusable description.
    /// </summary>
    public Query Query(in QueryDescription description)
    {
        var coreDescription = description.ToCore();
        return new Query(_world.Query(in coreDescription));
    }
}
