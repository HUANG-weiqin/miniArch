namespace MiniArch.Core;

/// <summary>
/// Merged entity metadata: version + location in one cache line.
/// Stores both Archetype and Chunk references to avoid the Chunk→Owner
/// indirection on every entity metadata access. Fields are ordered for
/// natural 24-byte Sequential layout:
/// Archetype(8) + Chunk(8) + RowIndex(4) + Version(4) = 24 bytes.
/// </summary>
internal struct EntityRecord
{
    public Archetype? Archetype; // offset 0, 8 bytes; null = unoccupied
    public Chunk? Chunk;         // offset 8, 8 bytes
    public int RowIndex;         // offset 16, 4 bytes
    public int Version;          // offset 20, 4 bytes

    public readonly bool IsOccupied => Chunk is not null;
}
