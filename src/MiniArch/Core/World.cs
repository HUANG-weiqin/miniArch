using System.Buffers;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public sealed class World
{
    private const int StackAllocatedBatchRangeLimit = 128;

    private readonly ComponentRegistry _components = new();
    private readonly Dictionary<Signature, Archetype> _archetypes = new();
    private readonly List<int> _versions;
    private readonly List<EntityInfo?> _locations;
    private readonly Stack<int> _freeIds = new();
    private readonly Dictionary<QueryFilter, Query> _queries = new();
    private readonly int _chunkCapacity;
    private int _archetypeGeneration;

    public World(int chunkCapacity = 128, int entityCapacity = 64)
    {
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        }

        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

        _chunkCapacity = chunkCapacity;
        _versions = new List<int>(entityCapacity);
        _locations = new List<EntityInfo?>(entityCapacity);
    }

    public ComponentRegistry Components => _components;

    public int EntityCapacity => _versions.Capacity;

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

    public void CreateMany(Span<Entity> entities)
    {
        if (entities.Length == 0)
        {
            return;
        }

        if (_freeIds.Count == 0)
        {
            CreateManyFresh(entities);
            return;
        }

        FillCreatedEntities(entities);

        var archetype = GetOrCreateArchetype(Signature.Empty);
        var maxRangeCount = Math.Min(entities.Length, archetype.Chunks.Count + ((entities.Length + _chunkCapacity - 1) / _chunkCapacity));

        if (maxRangeCount <= StackAllocatedBatchRangeLimit)
        {
            Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[StackAllocatedBatchRangeLimit];
            var rangeCount = archetype.ReserveEntities(entities, ranges);
            WriteBatchLocations(archetype, entities, ranges[..rangeCount]);
            return;
        }

        var rentedRanges = ArrayPool<EntityBatchRange>.Shared.Rent(maxRangeCount);
        try
        {
            var ranges = rentedRanges.AsSpan(0, maxRangeCount);
            var rangeCount = archetype.ReserveEntities(entities, ranges);
            WriteBatchLocations(archetype, entities, ranges[..rangeCount]);
        }
        finally
        {
            ArrayPool<EntityBatchRange>.Shared.Return(rentedRanges);
        }
    }

    public void EnsureCapacity(int entityCapacity)
    {
        if (entityCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCapacity));
        }

        _versions.EnsureCapacity(entityCapacity);
        _locations.EnsureCapacity(entityCapacity);
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
        var componentType = GetComponentType<T>();
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            return;
        }

        if (archetype.Edges.TryGetAdd(componentType, out var cached) && cached is not null)
        {
            MoveEntity(entity, info, cached, componentType, in component);
            return;
        }

        var destinationSignature = archetype.Signature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: true);
        MoveEntity(entity, info, destination, componentType, in component);
    }

    public void Set<T>(Entity entity, T component)
    {
        var componentType = GetComponentType<T>();
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (archetype.TryGetComponentIndex(componentType, out var componentIndex))
        {
            archetype.GetChunk(info.ChunkIndex).SetComponentAtTyped(componentIndex, info.RowIndex, in component);
            return;
        }

        var destinationSignature = archetype.Signature.Add(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: true);
        MoveEntity(entity, info, destination, componentType, in component);
    }

    public void Remove<T>(Entity entity)
    {
        var componentType = GetComponentType<T>();
        var info = GetRequiredLocation(entity);
        var archetype = info.Archetype;

        if (!archetype.TryGetComponentIndex(componentType, out _))
        {
            return;
        }

        if (archetype.Edges.TryGetRemove(componentType, out var cached) && cached is not null)
        {
            MoveEntity(entity, info, cached);
            return;
        }

        var destinationSignature = archetype.Signature.Remove(componentType);
        var destination = GetOrCreateDestinationArchetype(archetype, componentType, destinationSignature, isAdd: false);
        MoveEntity(entity, info, destination);
    }

    public QueryBuilder Query()
    {
        return new QueryBuilder(this, QueryFilter.Empty);
    }

    public Query Query<T1>()
    {
        var componentType = GetComponentType<T1>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType));
    }

    public Query Query<T1, T2>()
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        return GetOrCreateQuery(QueryFilter.CreateRequired(componentType1, componentType2));
    }

    public Query Query<T1, T2, T3>()
    {
        var componentType1 = GetComponentType<T1>();
        var componentType2 = GetComponentType<T2>();
        var componentType3 = GetComponentType<T3>();
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
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        var destinationChunk = destination.ReserveEntity(entity, out var destinationChunkIndex, out var destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);

        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = new EntityInfo(movedEntity.Version, sourceInfo.Archetype, sourceInfo.ChunkIndex, sourceInfo.RowIndex);
        }

        _locations[entity.Id] = new EntityInfo(entity.Version, destination, destinationChunkIndex, destinationRowIndex);
    }

    private void MoveEntity<T>(Entity entity, EntityInfo sourceInfo, Archetype destination, ComponentType componentType, in T componentValue)
    {
        var sourceChunk = sourceInfo.Archetype.GetChunk(sourceInfo.ChunkIndex);
        var destinationChunk = destination.ReserveEntity(entity, out var destinationChunkIndex, out var destinationRowIndex);
        destinationChunk.CopySharedComponentsFrom(sourceChunk, sourceInfo.RowIndex, destinationRowIndex);

        var destinationColumnIndex = destination.GetComponentIndex(componentType);
        destinationChunk.SetComponentAtTyped(destinationColumnIndex, destinationRowIndex, in componentValue);

        sourceInfo.Archetype.RemoveEntity(sourceInfo.ChunkIndex, sourceInfo.RowIndex, out var movedEntity);

        if (movedEntity.IsValid)
        {
            _locations[movedEntity.Id] = new EntityInfo(movedEntity.Version, sourceInfo.Archetype, sourceInfo.ChunkIndex, sourceInfo.RowIndex);
        }

        _locations[entity.Id] = new EntityInfo(entity.Version, destination, destinationChunkIndex, destinationRowIndex);
    }

    private Archetype GetOrCreateDestinationArchetype(Archetype source, ComponentType componentType, Signature destinationSignature, bool isAdd)
    {
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

        archetype = new Archetype(signature, ResolveComponentTypes(signature), _chunkCapacity);
        _archetypes.Add(signature, archetype);
        _archetypeGeneration++;
        return archetype;
    }

    private Type[] ResolveComponentTypes(Signature signature)
    {
        var componentCount = signature.Count;
        if (componentCount == 0)
        {
            return Array.Empty<Type>();
        }

        var types = new Type[componentCount];
        var components = signature.AsSpan();
        for (var index = 0; index < componentCount; index++)
        {
            types[index] = _components.GetType(components[index]);
        }

        return types;
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

    private void FillCreatedEntities(Span<Entity> entities)
    {
        var reusedCount = Math.Min(entities.Length, _freeIds.Count);
        for (var index = 0; index < reusedCount; index++)
        {
            var id = _freeIds.Pop();
            entities[index] = new Entity(id, _versions[id]);
        }

        var newEntityCount = entities.Length - reusedCount;
        if (newEntityCount == 0)
        {
            return;
        }

        var startId = _versions.Count;
        var requiredCount = startId + newEntityCount;
        EnsureCapacity(requiredCount);
        CollectionsMarshal.SetCount(_versions, requiredCount);
        CollectionsMarshal.SetCount(_locations, requiredCount);

        for (var index = 0; index < newEntityCount; index++)
        {
            entities[reusedCount + index] = new Entity(startId + index, 0);
        }
    }

    private void CreateManyFresh(Span<Entity> entities)
    {
        var startId = _versions.Count;
        var requiredCount = startId + entities.Length;
        EnsureCapacity(requiredCount);
        CollectionsMarshal.SetCount(_versions, requiredCount);
        CollectionsMarshal.SetCount(_locations, requiredCount);

        var archetype = GetOrCreateArchetype(Signature.Empty);
        var maxRangeCount = Math.Min(entities.Length, archetype.Chunks.Count + ((entities.Length + _chunkCapacity - 1) / _chunkCapacity));

        if (maxRangeCount <= StackAllocatedBatchRangeLimit)
        {
            Span<EntityBatchRange> ranges = stackalloc EntityBatchRange[StackAllocatedBatchRangeLimit];
            var rangeCount = archetype.ReserveEntityRanges(entities.Length, ranges);
            WriteFreshEntitiesAndLocations(archetype, startId, entities, ranges[..rangeCount]);
            return;
        }

        var rentedRanges = ArrayPool<EntityBatchRange>.Shared.Rent(maxRangeCount);
        try
        {
            var ranges = rentedRanges.AsSpan(0, maxRangeCount);
            var rangeCount = archetype.ReserveEntityRanges(entities.Length, ranges);
            WriteFreshEntitiesAndLocations(archetype, startId, entities, ranges[..rangeCount]);
        }
        finally
        {
            ArrayPool<EntityBatchRange>.Shared.Return(rentedRanges);
        }
    }

    private void WriteBatchLocations(Archetype archetype, ReadOnlySpan<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges)
    {
        var locations = CollectionsMarshal.AsSpan(_locations);
        var entityIndex = 0;
        foreach (var range in ranges)
        {
            for (var rowOffset = 0; rowOffset < range.Count; rowOffset++)
            {
                var entity = entities[entityIndex++];
                locations[entity.Id] = new EntityInfo(entity.Version, archetype, range.ChunkIndex, range.StartRow + rowOffset);
            }
        }
    }

    private void WriteFreshEntitiesAndLocations(Archetype archetype, int startId, Span<Entity> entities, ReadOnlySpan<EntityBatchRange> ranges)
    {
        var locations = CollectionsMarshal.AsSpan(_locations);
        var entityIndex = 0;
        var nextId = startId;

        foreach (var range in ranges)
        {
            var chunkEntities = archetype.GetChunk(range.ChunkIndex).GetReservedEntities(range.StartRow, range.Count);
            for (var rowOffset = 0; rowOffset < range.Count; rowOffset++)
            {
                var entity = new Entity(nextId++, 0);
                entities[entityIndex++] = entity;
                chunkEntities[rowOffset] = entity;
                locations[entity.Id] = new EntityInfo(entity.Version, archetype, range.ChunkIndex, range.StartRow + rowOffset);
            }
        }
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

    private ComponentType GetComponentType<T>()
    {
        if (ComponentTypeCache<T>.Registry != _components)
        {
            ComponentTypeCache<T>.Registry = _components;
            ComponentTypeCache<T>.ComponentType = _components.GetOrCreate<T>();
        }

        return ComponentTypeCache<T>.ComponentType;
    }

    private static class ComponentTypeCache<T>
    {
        public static ComponentRegistry? Registry;
        public static ComponentType ComponentType;
    }
}
