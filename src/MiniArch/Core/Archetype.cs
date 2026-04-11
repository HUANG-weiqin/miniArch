namespace MiniArch.Core;

public sealed class Archetype
{
    private readonly List<Chunk> _chunks = new();
    private readonly int _chunkCapacity;

    public Archetype(Signature signature, int chunkCapacity = 4)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (chunkCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCapacity));
        }

        Signature = signature;
        _chunkCapacity = chunkCapacity;
        _chunks.Add(new Chunk(signature, chunkCapacity));
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

    public bool RemoveEntity(int chunkIndex, int rowIndex, out Entity movedEntity)
    {
        var moved = _chunks[chunkIndex].RemoveAt(rowIndex, out movedEntity);
        EntityCount--;
        return moved;
    }

    public Chunk GetChunk(int chunkIndex) => _chunks[chunkIndex];

    private Chunk GetWritableChunk(out int chunkIndex)
    {
        chunkIndex = _chunks.Count - 1;
        var chunk = _chunks[chunkIndex];
        if (chunk.Count < chunk.Capacity)
        {
            return chunk;
        }

        chunk = new Chunk(Signature, _chunkCapacity);
        _chunks.Add(chunk);
        chunkIndex = _chunks.Count - 1;
        return chunk;
    }
}
