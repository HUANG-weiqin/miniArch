namespace MiniArch.Core;

/// <summary>
/// Stores entities for one signature.
/// </summary>
public sealed class Archetype
{
    private readonly List<Chunk> _chunks = new();
    private readonly int _chunkCapacity;
    private readonly Type[] _componentTypes;
    private readonly int[] _componentIdToColumnIndex;

    internal Archetype(Signature signature, Type[] componentTypes, int chunkCapacity = 4)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        }

        if (componentTypes.Length != signature.Count)
        {
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));
        }

        Signature = signature;
        _chunkCapacity = chunkCapacity;
        _componentTypes = componentTypes;
        _componentIdToColumnIndex = ComponentColumnMap.Build(signature);
        _chunks.Add(CreateChunk());
    }

    /// <summary>
    /// Gets the archetype signature.
    /// </summary>
    public Signature Signature { get; }

    /// <summary>
    /// Gets the entity count.
    /// </summary>
    public int EntityCount { get; private set; }

    /// <summary>
    /// Gets the chunk list.
    /// </summary>
    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <summary>
    /// Gets the cached edges.
    /// </summary>
    public ArchetypeEdges Edges { get; } = new();

    /// <summary>
    /// Adds an entity with components.
    /// </summary>
    internal void AddEntity(Entity entity, IReadOnlyDictionary<ComponentType, object?> components, out int chunkIndex, out int rowIndex)
    {
        var chunk = GetWritableChunk(out chunkIndex);
        rowIndex = chunk.Add(entity, components);
        EntityCount++;
    }

    /// <summary>
    /// Reserves a row for an entity.
    /// </summary>
    internal Chunk ReserveEntity(Entity entity, out int chunkIndex, out int rowIndex)
    {
        var chunk = GetWritableChunk(out chunkIndex);
        rowIndex = chunk.Add(entity);
        EntityCount++;
        return chunk;
    }

    internal Chunk ImportSnapshotChunk(ReadOnlySpan<Entity> entities, out int chunkIndex)
    {
        if (entities.Length == 0)
        {
            if (_chunks.Count == 0)
            {
                var emptyChunk = CreateChunk();
                _chunks.Add(emptyChunk);
                chunkIndex = 0;
                return emptyChunk;
            }

            chunkIndex = _chunks.Count - 1;
            return _chunks[chunkIndex];
        }

        Chunk chunk;
        if (_chunks.Count == 1 && _chunks[0].Count == 0 && EntityCount == 0)
        {
            chunk = _chunks[0];
            chunkIndex = 0;
        }
        else
        {
            chunk = CreateChunk();
            _chunks.Add(chunk);
            chunkIndex = _chunks.Count - 1;
        }

        var startRow = chunk.ReserveRows(entities.Length);
        entities.CopyTo(chunk.GetReservedEntities(startRow, entities.Length));
        EntityCount += entities.Length;
        return chunk;
    }

    internal int ReserveEntityRanges(int entityCount, Span<EntityBatchRange> ranges)
    {
        if (entityCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entityCount));
        }

        if (entityCount == 0)
        {
            return 0;
        }

        var remaining = entityCount;
        var rangeCount = 0;

        for (var chunkIndex = _chunks.Count - 1; chunkIndex >= 0 && remaining > 0; chunkIndex--)
        {
            var chunk = _chunks[chunkIndex];
            var available = chunk.Capacity - chunk.Count;
            if (available <= 0)
            {
                continue;
            }

            var fillCount = Math.Min(available, remaining);
            var startRow = chunk.ReserveRows(fillCount);
            ranges[rangeCount++] = new EntityBatchRange(chunkIndex, startRow, fillCount);
            remaining -= fillCount;
        }

        while (remaining > 0)
        {
            var chunk = CreateChunk();
            _chunks.Add(chunk);

            var fillCount = Math.Min(chunk.Capacity, remaining);
            chunk.ReserveRows(fillCount);
            ranges[rangeCount++] = new EntityBatchRange(_chunks.Count - 1, 0, fillCount);
            remaining -= fillCount;
        }

        EntityCount += entityCount;
        return rangeCount;
    }

    /// <summary>
    /// Removes an entity by location.
    /// </summary>
    internal bool RemoveEntity(int chunkIndex, int rowIndex, out Entity movedEntity)
    {
        var moved = _chunks[chunkIndex].RemoveAt(rowIndex, out movedEntity);
        EntityCount--;
        return moved;
    }

    /// <summary>
    /// Gets a chunk by index.
    /// </summary>
    public Chunk GetChunk(int chunkIndex) => _chunks[chunkIndex];

    internal bool TryGetComponentIndex(ComponentType component, out int columnIndex)
    {
        var componentId = component.Value;
        if ((uint)componentId >= (uint)_componentIdToColumnIndex.Length)
        {
            columnIndex = -1;
            return false;
        }

        columnIndex = _componentIdToColumnIndex[componentId];
        return columnIndex >= 0;
    }

    internal int GetComponentIndex(ComponentType component)
    {
        if (TryGetComponentIndex(component, out var columnIndex))
        {
            return columnIndex;
        }

        throw new ArgumentException($"Archetype does not contain component {component.Value}.", nameof(component));
    }

    private Chunk CreateChunk()
    {
        return new Chunk(Signature, _componentTypes, _componentIdToColumnIndex, _chunkCapacity);
    }

    private Chunk GetWritableChunk(out int chunkIndex)
    {
        for (chunkIndex = _chunks.Count - 1; chunkIndex >= 0; chunkIndex--)
        {
            var existingChunk = _chunks[chunkIndex];
            if (existingChunk.Count < existingChunk.Capacity)
            {
                return existingChunk;
            }
        }

        var chunk = CreateChunk();
        _chunks.Add(chunk);
        chunkIndex = _chunks.Count - 1;
        return chunk;
    }

}
