namespace MiniArch.Core;

public readonly record struct EntityInfo(int Version, Archetype Archetype, int ChunkIndex, int RowIndex);
