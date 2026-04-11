namespace MiniArch.Core;

public sealed class Archetype
{
    private readonly List<Chunk> _chunks = new();
    private readonly int _chunkCapacity;
    private readonly Type[]? _componentTypes;
    private readonly int[] _componentIdToColumnIndex;

    public Archetype(Signature signature, int chunkCapacity = 4)
        : this(signature, null, chunkCapacity, false)
    {
    }

    internal Archetype(Signature signature, Type[] componentTypes, int chunkCapacity = 4)
        : this(signature, componentTypes, chunkCapacity, true)
    {
    }

    private Archetype(Signature signature, Type[]? componentTypes, int chunkCapacity, bool typedColumns)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        }

        if (componentTypes is not null && componentTypes.Length != signature.Count)
        {
            throw new ArgumentException("Component type count must match signature count.", nameof(componentTypes));
        }

        Signature = signature;
        _chunkCapacity = chunkCapacity;
        _componentTypes = componentTypes;
        _componentIdToColumnIndex = BuildComponentIdToColumnIndex(signature);
        _chunks.Add(CreateChunk(typedColumns));
    }

    public Signature Signature { get; }

    public int EntityCount { get; private set; }

    public IReadOnlyList<Chunk> Chunks => _chunks;

    public ArchetypeEdges Edges { get; } = new();

    public void AddEntity(Entity entity, IReadOnlyDictionary<ComponentType, object?> components, out int chunkIndex, out int rowIndex)
    {
        var chunk = GetWritableChunk(out chunkIndex);
        rowIndex = chunk.Add(entity, components);
        EntityCount++;
    }

    public Chunk ReserveEntity(Entity entity, out int chunkIndex, out int rowIndex)
    {
        var chunk = GetWritableChunk(out chunkIndex);
        rowIndex = chunk.Add(entity);
        EntityCount++;
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
            var chunk = CreateChunk(_componentTypes is not null);
            _chunks.Add(chunk);

            var fillCount = Math.Min(chunk.Capacity, remaining);
            chunk.ReserveRows(fillCount);
            ranges[rangeCount++] = new EntityBatchRange(_chunks.Count - 1, 0, fillCount);
            remaining -= fillCount;
        }

        EntityCount += entityCount;
        return rangeCount;
    }

    public bool RemoveEntity(int chunkIndex, int rowIndex, out Entity movedEntity)
    {
        var moved = _chunks[chunkIndex].RemoveAt(rowIndex, out movedEntity);
        EntityCount--;
        return moved;
    }

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

    private Chunk CreateChunk(bool typedColumns)
    {
        if (!typedColumns)
        {
            return new Chunk(Signature, _componentIdToColumnIndex, _chunkCapacity);
        }

        ArgumentNullException.ThrowIfNull(_componentTypes);
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

        var chunk = CreateChunk(_componentTypes is not null);
        _chunks.Add(chunk);
        chunkIndex = _chunks.Count - 1;
        return chunk;
    }

    private static int[] BuildComponentIdToColumnIndex(Signature signature)
    {
        var components = signature.AsSpan();
        var maxComponentId = -1;
        for (var index = 0; index < components.Length; index++)
        {
            var componentId = components[index].Value;
            if (componentId > maxComponentId)
            {
                maxComponentId = componentId;
            }
        }

        if (maxComponentId < 0)
        {
            return Array.Empty<int>();
        }

        var lookup = new int[maxComponentId + 1];
        Array.Fill(lookup, -1);

        for (var index = 0; index < components.Length; index++)
        {
            lookup[components[index].Value] = index;
        }

        return lookup;
    }
}
