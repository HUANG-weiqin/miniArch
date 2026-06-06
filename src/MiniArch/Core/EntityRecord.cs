namespace MiniArch.Core;

/// <summary>
/// Merged entity metadata: version + location in one cache line.
/// Replaces the separate _versions[] and _locations[] arrays to reduce
/// random-access cache misses from 2 to 1.
///
/// ArchetypeIndexPlusOne uses 0 as the sentinel for unoccupied/default,
/// matching the natural default value of arrays. This avoids the need to
/// initialize records with a non-zero sentinel.
/// </summary>
internal struct EntityRecord
{
    public int Version;
    public int ArchetypeIndexPlusOne; // 0 = unoccupied, 1+ = index into World._archetypesByIndex
    public int ChunkIndex;
    public int RowIndex;

    public readonly bool IsOccupied => ArchetypeIndexPlusOne != 0;
}
