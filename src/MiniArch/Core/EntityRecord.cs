namespace MiniArch.Core;

/// <summary>
/// Merged entity metadata: version + location in one cache line.
/// Stores Chunk reference directly to skip the Archetype→chunks[chunkIndex] indirection
/// on every entity access. Fields are ordered for natural 16-byte Sequential layout:
/// Chunk(8) + RowIndex(4) + Version(4) = 16 bytes.
/// </summary>
internal struct EntityRecord
{
    public Chunk? Chunk;       // offset 0, 8 bytes; null = unoccupied
    public int RowIndex;       // offset 8, 4 bytes
    public int Version;        // offset 12, 4 bytes

    public readonly bool IsOccupied => Chunk is not null;
}
