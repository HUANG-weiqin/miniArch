namespace MiniArch.Core;

internal readonly record struct EntityLocation(Archetype Archetype, int ChunkIndex, int RowIndex);
