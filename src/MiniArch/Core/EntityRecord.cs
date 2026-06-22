namespace MiniArch.Core;

/// <summary>
/// Entity metadata: version + location packed into 16 bytes (a quarter of a
/// 64-byte cache line). Archetype is the sole storage unit (no multi-chunk
/// splitting), so it alone identifies the storage block.
/// Fields ordered for natural Sequential layout:
/// Archetype(8) + RowIndex(4) + Version(4) = 16 bytes — four records fit per
/// cache line, so iterating <c>_records</c> is cache-dense.
/// </summary>
internal struct EntityRecord
{
    public Archetype? Archetype; // null = unoccupied
    public int RowIndex;
    public int Version;

    public readonly bool IsOccupied => Archetype is not null;
}
