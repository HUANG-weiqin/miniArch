namespace MiniArch.Core;

internal readonly record struct EntityBatchRange(int ChunkIndex, int StartRow, int Count);
