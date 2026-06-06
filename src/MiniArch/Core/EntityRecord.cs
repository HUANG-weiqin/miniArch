namespace MiniArch.Core;

/// <summary>
/// Entity metadata: version + location in one cache line.
/// Archetype is the sole storage unit (no multi-chunk splitting),
/// so Chunk is removed — Archetype alone identifies the storage block.
/// Fields ordered for natural 16-byte Sequential layout:
/// Archetype(8) + RowIndex(4) + Version(4) = 16 bytes.
/// </summary>
internal struct EntityRecord
{
    public Archetype? Archetype; // null = unoccupied
    public int RowIndex;
    public int Version;

    public readonly bool IsOccupied => Archetype is not null;
}
