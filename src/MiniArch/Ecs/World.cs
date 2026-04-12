using System.Linq;

namespace MiniArch.Ecs;

public sealed class World
{
    private readonly MiniArch.Core.World _world;

    public World(int chunkCapacity = 128, int entityCapacity = 64)
    {
        _world = new MiniArch.Core.World(chunkCapacity, entityCapacity);
    }

    internal World(MiniArch.Core.World world)
    {
        _world = world;
    }

    public MiniArch.Core.World Advanced => _world;

    public Entity Create() => Entity.FromCore(_world.Create());

    public Entity Create<T1>(T1 component1) => Entity.FromCore(_world.Create(component1));

    public Entity Create<T1, T2>(T1 component1, T2 component2) => Entity.FromCore(_world.Create(component1, component2));

    public Entity Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3) => Entity.FromCore(_world.Create(component1, component2, component3));

    public void Add<T>(Entity entity, T component) => _world.Add(entity.AsCore(), component);

    public void Set<T>(Entity entity, T component) => _world.Set(entity.AsCore(), component);

    public void Remove<T>(Entity entity) => _world.Remove<T>(entity.AsCore());

    public void Destroy(Entity entity) => _world.Destroy(entity.AsCore());

    public void Link(Entity parent, Entity child) => _world.Link(parent.AsCore(), child.AsCore());

    public void Unlink(Entity child) => _world.Unlink(child.AsCore());

    public bool TryGetParent(Entity child, out Entity parent)
    {
        var result = _world.TryGetParent(child.AsCore(), out var resolved);
        parent = Entity.FromCore(resolved);
        return result;
    }

    public List<Entity> GetChildren(Entity parent)
    {
        return _world.GetChildren(parent.AsCore()).Select(Entity.FromCore).ToList();
    }

    public bool IsAlive(Entity entity) => _world.IsAlive(entity.AsCore());

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

    public Query<T> Query<T>()
    {
        return new Query<T>(_world.Query<T>(), _world.Components.GetOrCreate<T>());
    }

    public Query<T1, T2> Query<T1, T2>()
    {
        return new Query<T1, T2>(
            _world.Query<T1, T2>(),
            _world.Components.GetOrCreate<T1>(),
            _world.Components.GetOrCreate<T2>());
    }
}
