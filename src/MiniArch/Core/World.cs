namespace MiniArch.Core;

public sealed class World
{
    private readonly ComponentRegistry _components = new();
    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    private readonly List<int> _versions = new();
    private readonly List<EntityInfo?> _locations = new();
    private readonly Stack<int> _freeIds = new();
    private readonly Dictionary<QueryFilter, Query> _queries = new();
    private readonly int _chunkCapacity;
    private int _archetypeGeneration;

    public World(int chunkCapacity = 4)
    {
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        }

        _chunkCapacity = chunkCapacity;
    }

    public ComponentRegistry Components => _components;

    internal Dictionary<Signature, Archetype>.ValueCollection Archetypes => _archetypes.Values;

    internal int ArchetypeGeneration => _archetypeGeneration;

    public Entity Create()
    {
        var id = AcquireEntityId();
        var version = _versions[id];
        var entity = new Entity(id, version);
        var archetype = GetOrCreateArchetype(Signature.Empty);
        archetype.ReserveEntity(entity, out var chunkIndex, out var rowIndex);
        _locations[id] = new EntityInfo(version, archetype, chunkIndex, rowIndex);
        return entity;
    }

    public void Destroy(Entity entity)
    {
        var info = GetRequiredLocation(entity);
        info.Archetype.RemoveEntity(info.ChunkIndex, info.RowIndex, out var movedEntity);
        _locations[entity.Id] = null;
        _versions[entity.Id] = entity.Version + 1;
        _freeIds.Push(entity.Id);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = info with { Version = movedEntity.Version };
        }
    }

    public void Add<T>(Entity entity, T component)
    {
        var componentType = _components.GetOrCreate<T>();
        var info = GetRequiredLocation(entity);
        var sourceSignature = info.Archetype.Signature;

        if (sourceSignature.Contains(componentType))
        {
            info.Archetype.GetChunk(info.ChunkIndex).SetComponent(componentType, info.RowIndex, component);
            return;
        }

        var destinationSignature = sourceSignature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(info.Archetype, componentType, destinationSignature, isAdd: true);
        MoveEntity(entity, info, destination, componentType, component);
    }

    public void Set<T>(Entity entity, T component)
    {
        var componentType = _components.GetOrCreate<T>();
        var info = GetRequiredLocation(entity);
        var sourceSignature = info.Archetype.Signature;

        if (sourceSignature.Contains(componentType))
        {
            info.Archetype.GetChunk(info.ChunkIndex).SetComponent(componentType, info.RowIndex, component);
            return;
        }

        var destinationSignature = sourceSignature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(info.Archetype, componentType, destinationSignature, isAdd: true);
        MoveEntity(entity, info, destination, componentType, component);
    }

    public void Remove<T>(Entity entity)
    {
        var componentType = _components.GetOrCreate<T>();
        var info = GetRequiredLocation(entity);
        var sourceSignature = info.Archetype.Signature;

        if (!sourceSignature.Contains(componentType))
        {
            return;
        }

        var destinationSignature = sourceSignature.Remove(componentType);
        var destination = GetOrCreateDestinationArchetype(info.Archetype, componentType, destinationSignature, isAdd: false);
        MoveEntity(entity, info, destination);
    }

    public QueryBuilder Query()
    {
        return new QueryBuilder(this, QueryFilter.Empty);
    }

    public Query Query<T1>()
    {
        var componentType = _components.GetOrCreate<T1>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType));
    }

    public Query Query<T1, T2>()
    {
        var componentType1 = _components.GetOrCreate<T1>();
        var componentType2 = _components.GetOrCreate<T2>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType1, componentType2));
    }

    public Query Query<T1, T2, T3>()
    {
        var componentType1 = _components.GetOrCreate<T1>();
        var componentType2 = _components.GetOrCreate<T2>();
        var componentType3 = _components.GetOrCreate<T3>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType1, componentType2, componentType3));
    }

    public bool TryGetLocation(Entity entity, out EntityInfo info)
    {
        if (entity.Id < 0 || entity.Id >= _locations.Count)
        {
            info = default;
            return false;
        }

        var stored = _locations[entity.Id];
        if (stored is null || stored.Value.Version != entity.Version)
        {
            info = default;
            return false;
        }

        info = stored.Value;
        return true;
    }

    private void MoveEntity(Entity entity, EntityInfo sourceInfo, Archetype destination)
    {
        MoveEntity(entity, sourceInfo, destination, default, null, hasOverride: false);
    }

    private void MoveEntity(Entity entity, EntityInfo sourceInfo, Archetype destination, ComponentType componentType, object? componentValue)
    {
        MoveEntity(entity, sourceInfo, destination, componentType, componentValue, hasOverride: true);
    }

    private void MoveEntity(Entity entity, EntityInfo sourceInfo, Archetype destination, ComponentType componentType, object? componentValue, bool hasOverride)
    {
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        var destinationChunk = destination.ReserveEntity(entity, out var destinationChunkIndex, out var destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);

        if (hasOverride)
        {
            destinationChunk.SetComponent(componentType, destinationRowIndex, componentValue);
        }

        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = new EntityInfo(movedEntity.Version, sourceInfo.Archetype, sourceInfo.ChunkIndex, sourceInfo.RowIndex);
        }

        _locations[entity.Id] = new EntityInfo(entity.Version, destination, destinationChunkIndex, destinationRowIndex);
    }

    private Archetype GetOrCreateDestinationArchetype(Archetype source, ComponentType componentType, Signature destinationSignature, bool isAdd)
    {
        if (isAdd)
        {
            if (source.Edges.TryGetAdd(componentType, out var cached) && cached is not null)
            {
                return cached;
            }
        }
        else
        {
            if (source.Edges.TryGetRemove(componentType, out var cached) && cached is not null)
            {
                return cached;
            }
        }

        var destination = GetOrCreateArchetype(destinationSignature);
        if (isAdd)
        {
            source.Edges.CacheAdd(componentType, destination);
            destination.Edges.CacheRemove(componentType, source);
        }
        else
        {
            source.Edges.CacheRemove(componentType, destination);
            destination.Edges.CacheAdd(componentType, source);
        }

        return destination;
    }

    private Archetype GetOrCreateArchetype(Signature signature)
    {
        if (_archetypes.TryGetValue(signature, out var archetype))
        {
            return archetype;
        }

        archetype = new Archetype(signature, _chunkCapacity);
        _archetypes.Add(signature, archetype);
        _archetypeGeneration++;
        return archetype;
    }

    internal Query GetOrCreateQuery(QueryFilter filter)
    {
        if (_queries.TryGetValue(filter, out var query))
        {
            return query;
        }

        query = new Query(this, filter);
        _queries.Add(filter, query);
        return query;
    }

    private EntityInfo GetRequiredLocation(Entity entity)
    {
        if (!TryGetLocation(entity, out var info))
        {
            throw new InvalidOperationException($"Entity {entity} is stale or unknown.");
        }

        return info;
    }

    private int AcquireEntityId()
    {
        if (_freeIds.Count > 0)
        {
            return _freeIds.Pop();
        }

        var id = _versions.Count;
        _versions.Add(0);
        _locations.Add(null);
        return id;
    }
}
