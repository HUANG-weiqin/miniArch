namespace MiniArch.Core;

/// <summary>
/// Merged entity metadata: version + location in one cache line.
/// Replaces the separate _versions[] and _locations[] arrays to reduce
/// random-access cache misses from 2 to 1.
/// </summary>
internal struct EntityRecord
{
    public int Version;
    public Archetype? Archetype;
    public int ChunkIndex;
    public int RowIndex;

    public readonly bool IsOccupied => Archetype is not null;
}
