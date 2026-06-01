using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

/// <summary>
/// Stores entities for one signature.
/// </summary>
public sealed class Archetype
{
    private readonly List<Chunk> _chunks = new();
    private int[] _nonFullChunkIndexes = [];
    private int _nonFullCount;
    private bool[] _chunkHasNonFullEntry = [];
    private readonly int _chunkCapacity;
    private readonly Type[] _componentTypes;
    private readonly int[] _componentIdToColumnIndex;
    private long _generation;

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
        AddChunk();
        MarkChunkNonFull(0);
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
    /// Gets the archetype generation (incremented on structural changes).
    /// </summary>
    internal long Generation => _generation;

    /// <summary>
    /// Gets the chunk list.
    /// </summary>
    public IReadOnlyList<Chunk> Chunks => _chunks;

    internal ArchetypeEdges Edges { get; } = new();

    /// <summary>
    /// Adds an entity with components.
    /// </summary>
    internal void AddEntity(Entity entity, IReadOnlyDictionary<ComponentType, object?> components, out int chunkIndex, out int rowIndex)
    {
        var chunk = GetWritableChunk(out chunkIndex);
        rowIndex = chunk.Add(entity, components);
        MarkChunkNonFull(chunkIndex);
        EntityCount++;
    }

    /// <summary>
    /// Reserves a row for an entity.
    /// </summary>
    internal Chunk ReserveEntity(Entity entity, out int chunkIndex, out int rowIndex)
    {
        var chunk = GetWritableChunk(out chunkIndex);
        rowIndex = chunk.Add(entity);
        MarkChunkNonFull(chunkIndex);
        EntityCount++;
        _generation++;
        return chunk;
    }

    internal Chunk ImportSnapshotChunk(ReadOnlySpan<Entity> entities, out int chunkIndex)
    {
        if (entities.Length == 0)
        {
            if (_chunks.Count == 0)
            {
                var emptyChunk = AddChunk();
                MarkChunkNonFull(0);
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
            chunk = AddChunk();
            chunkIndex = _chunks.Count - 1;
        }

        var startRow = chunk.ReserveRows(entities.Length);
        MarkChunkNonFull(chunkIndex);
        entities.CopyTo(chunk.GetReservedEntities(startRow, entities.Length));
        EntityCount += entities.Length;
        _generation++;
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

        while (remaining > 0 && TryTakeNonFullChunk(out var chunkIndex, out var chunk))
        {
            var available = chunk.Capacity - chunk.Count;
            var fillCount = Math.Min(available, remaining);
            var startRow = chunk.ReserveRows(fillCount);
            MarkChunkNonFull(chunkIndex);
            ranges[rangeCount++] = new EntityBatchRange(chunkIndex, startRow, fillCount);
            remaining -= fillCount;
        }

        while (remaining > 0)
        {
            var chunk = AddChunk();
            var chunkIndex = _chunks.Count - 1;

            var fillCount = Math.Min(chunk.Capacity, remaining);
            chunk.ReserveRows(fillCount);
            MarkChunkNonFull(chunkIndex);
            ranges[rangeCount++] = new EntityBatchRange(chunkIndex, 0, fillCount);
            remaining -= fillCount;
        }

        EntityCount += entityCount;
        _generation++;
        return rangeCount;
    }

    /// <summary>
    /// Removes an entity by location.
    /// </summary>
    internal bool RemoveEntity(int chunkIndex, int rowIndex, out Entity movedEntity)
    {
        var moved = _chunks[chunkIndex].RemoveAt(rowIndex, out movedEntity);
        MarkChunkNonFull(chunkIndex);
        EntityCount--;
        _generation++;
        return moved;
    }

    /// <summary>
    /// Gets a chunk by index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Chunk GetChunk(int chunkIndex) => _chunks[chunkIndex];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<Chunk> GetChunkSpan() => CollectionsMarshal.AsSpan(_chunks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetComponentIndexFast(ComponentType component) => _componentIdToColumnIndex[component.Value];


    private Chunk CreateChunk()
    {
        return new Chunk(Signature, _componentTypes, _componentIdToColumnIndex, _chunkCapacity);
    }

    private Chunk AddChunk()
    {
        var chunk = CreateChunk();
        _chunks.Add(chunk);
        if (_chunks.Count > _chunkHasNonFullEntry.Length)
        {
            Array.Resize(ref _chunkHasNonFullEntry, _chunks.Count * 2);
        }
        _chunkHasNonFullEntry[_chunks.Count - 1] = false;
        return chunk;
    }

    private Chunk GetWritableChunk(out int chunkIndex)
    {
        if (TryTakeNonFullChunk(out chunkIndex, out var existingChunk))
        {
            return existingChunk;
        }

        var chunk = AddChunk();
        chunkIndex = _chunks.Count - 1;
        return chunk;
    }

    private bool TryTakeNonFullChunk(out int chunkIndex, out Chunk chunk)
    {
        while (_nonFullCount > 0)
        {
            var lastIndex = _nonFullCount - 1;
            chunkIndex = _nonFullChunkIndexes[lastIndex];
            _nonFullCount--;
            _chunkHasNonFullEntry[chunkIndex] = false;

            chunk = _chunks[chunkIndex];
            if (chunk.Count < chunk.Capacity)
            {
                return true;
            }
        }

        chunkIndex = -1;
        chunk = null!;
        return false;
    }

    private void MarkChunkNonFull(int chunkIndex)
    {
        var chunk = _chunks[chunkIndex];
        if (chunk.Count >= chunk.Capacity || _chunkHasNonFullEntry[chunkIndex])
        {
            return;
        }

        EnsureNonFullCapacity();
        _nonFullChunkIndexes[_nonFullCount++] = chunkIndex;
        _chunkHasNonFullEntry[chunkIndex] = true;
    }

    private void EnsureNonFullCapacity()
    {
        if (_nonFullCount < _nonFullChunkIndexes.Length) return;
        var newCapacity = _nonFullChunkIndexes.Length == 0 ? 4 : _nonFullChunkIndexes.Length * 2;
        Array.Resize(ref _nonFullChunkIndexes, newCapacity);
    }

}
